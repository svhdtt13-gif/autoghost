import json

import pytest

from qnyh_tool.profiles import ProfileError, load_profiles
from qnyh_tool.quests import Cadence, QuestError, load_catalog


def test_load_sample_profile_catalog():
    profiles = load_profiles("profiles/default.json")
    assert profiles[0].language == "vi"
    assert {locator.kind for locator in profiles[0].locators} == {"anchor", "ocr"}


def test_profile_rejects_absolute_coordinate_locator(tmp_path):
    path = tmp_path / "profiles.json"
    path.write_text(json.dumps({"profiles": [{"profile_id": "bad", "language": "vi", "skin": "default", "layout": "standard", "locators": [{"name": "x", "kind": "coordinate", "reference": "1,2"}]}]}), encoding="utf-8")
    with pytest.raises(ProfileError, match="kind must be one of"):
        load_profiles(path)


def test_load_sample_quest_catalog():
    catalog = load_catalog("tasks/catalog.json")
    assert len(catalog.quests) == 2
    assert catalog.quests[0].cadence is Cadence.DAILY


def test_quest_catalog_rejects_duplicate_ids(tmp_path):
    path = tmp_path / "catalog.json"
    quest = {"questId": "same", "aliases": ["Same"], "objective": "Do it", "destinations": ["place"], "completionSignals": ["done"], "cadence": "daily", "opensAt": "04:00", "expiresAt": "05:00"}
    path.write_text(json.dumps({"quests": [quest, quest]}), encoding="utf-8")
    with pytest.raises(QuestError, match="unique"):
        load_catalog(path)
