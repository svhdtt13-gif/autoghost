"""Dry-run quest plans built from recognized UI snapshots."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

from ..checkpoint import CheckpointStore
from ..observability import StructuredLogger
from ..quests import QuestDefinition
from .state_machine import Action, DryRunPort, ExecutionResult, StateMachine, Step


def build_quest_steps(quest: QuestDefinition, *, recognition_threshold: float = 0.8) -> tuple[Step, ...]:
    if not 0 <= recognition_threshold <= 1:
        raise ValueError("recognition_threshold must be between 0 and 1")
    destination = quest.destinations[0]
    completion_signal = quest.completion_signals[0]
    return (
        Step("select-quest", Action("select_quest", {"quest_id": quest.quest_id}), lambda snapshot: _recognized_quest(snapshot, quest.quest_id, recognition_threshold), lambda snapshot: snapshot.get("selected_quest_id") == quest.quest_id),
        Step("travel-to-destination", Action("travel", {"destination": destination}), lambda snapshot: snapshot.get("selected_quest_id") == quest.quest_id, lambda snapshot: snapshot.get("arrived_destination") == destination),
        Step("claim-reward", Action("claim_reward", {"completion_signal": completion_signal}), lambda snapshot: snapshot.get("completion_signal") in quest.completion_signals, lambda snapshot: snapshot.get("reward_claimed") is True),
    )


class DryRunQuestRunner:
    """Run a catalog quest against an in-memory simulation only."""

    def __init__(self, *, logger: StructuredLogger | None = None, checkpoints: CheckpointStore | None = None, session_id: str = "default", recognition_threshold: float = 0.8) -> None:
        if not 0 <= recognition_threshold <= 1:
            raise ValueError("recognition_threshold must be between 0 and 1")
        self._logger = logger
        self._checkpoints = checkpoints
        self._session_id = session_id
        self._recognition_threshold = recognition_threshold

    def run(self, client_id: str, quest: QuestDefinition, recognized_snapshot: Mapping[str, Any]) -> ExecutionResult:
        if not isinstance(recognized_snapshot, Mapping):
            raise TypeError("recognized_snapshot must be a mapping")
        simulated_snapshot = dict(recognized_snapshot)

        def simulate(action: Action) -> None:
            if action.name == "select_quest":
                simulated_snapshot["selected_quest_id"] = action.parameters["quest_id"]
            elif action.name == "travel":
                simulated_snapshot["arrived_destination"] = action.parameters["destination"]
            elif action.name == "claim_reward":
                simulated_snapshot["reward_claimed"] = True

        return StateMachine(DryRunPort(on_action=simulate), lambda: simulated_snapshot, logger=self._logger, checkpoints=self._checkpoints, session_id=self._session_id).run(client_id, build_quest_steps(quest, recognition_threshold=self._recognition_threshold))


def _recognized_quest(snapshot: Mapping[str, Any], quest_id: str, threshold: float) -> bool:
    confidence = snapshot.get("recognition_confidence")
    return snapshot.get("recognized_quest_id") == quest_id and isinstance(confidence, (int, float)) and not isinstance(confidence, bool) and confidence >= threshold
