from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from tools.ros_cli import build_registries, validate


def write_artifact(root: Path, relative: str, front_matter: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f"---\n{front_matter.strip()}\n---\n\n# Body\n", encoding="utf-8")


class RosCliTests(unittest.TestCase):
    def root(self) -> tuple[tempfile.TemporaryDirectory[str], Path]:
        temporary = tempfile.TemporaryDirectory()
        root = Path(temporary.name)
        (root / "registries").mkdir()
        return temporary, root

    def test_valid_small_artifact_set_passes(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        write_artifact(
            root,
            "research/evidence/EV-ROS-2026-A001--observation.md",
            """
id: EV-ROS-2026-A001
title: Observation
status: accepted
confidence: high
supports: [HY-ROS-2026-A002]
""",
        )
        write_artifact(
            root,
            "research/hypotheses/HY-ROS-2026-A002--claim.md",
            """
id: HY-ROS-2026-A002
title: Claim
status: supported
confidence: medium
supporting_evidence: [EV-ROS-2026-A001]
""",
        )
        self.assertEqual(build_registries(root), 0)
        self.assertEqual(validate(root), [])

    def test_malformed_front_matter_fails(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        path = root / "research/evidence/EV-ROS-2026-A001--bad.md"
        path.parent.mkdir(parents=True)
        path.write_text("---\nid: EV-ROS-2026-A001\n", encoding="utf-8")
        findings = validate(root, check_registries=False)
        self.assertTrue(any("missing closing" in finding.message for finding in findings))

    def test_duplicate_id_fails(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        metadata = """
id: EV-ROS-2026-A001
title: Duplicate
status: draft
"""
        write_artifact(root, "research/evidence/EV-ROS-2026-A001--one.md", metadata)
        write_artifact(root, "research/evidence/EV-ROS-2026-A001--two.md", metadata)
        findings = validate(root, check_registries=False)
        self.assertTrue(any("duplicate" in finding.message for finding in findings))

    def test_broken_evidence_reference_fails(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        write_artifact(
            root,
            "research/hypotheses/HY-ROS-2026-A001--broken.md",
            """
id: HY-ROS-2026-A001
title: Broken reference
status: proposed
confidence: low
supporting_evidence: [EV-ROS-2026-DEAD]
""",
        )
        findings = validate(root, check_registries=False)
        self.assertTrue(any(finding.field == "supporting_evidence" for finding in findings))

    def test_registry_build_is_deterministic_and_check_detects_staleness(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        write_artifact(
            root,
            "research/evidence/EV-ROS-2026-A001--observation.md",
            """
id: EV-ROS-2026-A001
title: Observation
status: draft
""",
        )
        self.assertEqual(build_registries(root), 0)
        first = (root / "registries/evidence.json").read_bytes()
        self.assertEqual(build_registries(root), 0)
        self.assertEqual(first, (root / "registries/evidence.json").read_bytes())
        (root / "registries/evidence.json").write_text("[]\n", encoding="utf-8")
        findings = validate(root)
        self.assertTrue(any("stale" in finding.message for finding in findings))

    def test_supersession_must_be_reciprocal(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        write_artifact(
            root,
            "research/evidence/EV-ROS-2026-A001--old.md",
            """
id: EV-ROS-2026-A001
title: Old
status: superseded
superseded_by: []
""",
        )
        write_artifact(
            root,
            "research/evidence/EV-ROS-2026-A002--new.md",
            """
id: EV-ROS-2026-A002
title: New
status: accepted
supersedes: [EV-ROS-2026-A001]
""",
        )
        findings = validate(root, check_registries=False)
        self.assertTrue(any("not reciprocal" in finding.message for finding in findings))

    def test_legacy_rep_identity_and_confidence_remain_compatible(self) -> None:
        temporary, root = self.root()
        self.addCleanup(temporary.cleanup)
        write_artifact(
            root,
            "research/packages/RP-2026-07-30-NHE-COMPARATIVE-REVIEW.md",
            """
identifier: RP-2026-07-30-NHE-COMPARATIVE-REVIEW
title: Legacy review
status: draft
confidence: medium-high
""",
        )
        self.assertEqual(validate(root, check_registries=False), [])


if __name__ == "__main__":
    unittest.main()


class ConceptAndGlossaryKindTests(unittest.TestCase):
    def test_concept_and_glossary_registries_are_optional_until_a_record_exists(self) -> None:
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            (root / "registries").mkdir()
            self.assertEqual(build_registries(root), 0)
            self.assertFalse((root / "registries/concepts.json").exists())
            self.assertFalse((root / "registries/glossary.json").exists())
            self.assertEqual(validate(root), [])

            write_artifact(
                root,
                "research/concepts/CN-ROS-2026-A1B2--work-item.md",
                """
id: CN-ROS-2026-A1B2
title: Work item
status: accepted
related_documents: [GL-ROS-2026-0001]
""",
            )
            write_artifact(
                root,
                "research/glossary/GL-ROS-2026-0001--checkpoint.md",
                """
id: GL-ROS-2026-0001
title: Checkpoint
status: draft
""",
            )
            stale = {finding.path for finding in validate(root) if "stale" in finding.message}
            self.assertEqual(stale, {"registries/concepts.json", "registries/glossary.json"})
            self.assertEqual(build_registries(root), 0)
            self.assertEqual(validate(root), [])

    def test_concept_and_glossary_rules_match_other_kinds(self) -> None:
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            (root / "registries").mkdir()
            write_artifact(
                root,
                "research/concepts/CN-ROS-2026-A1B2--work-item.md",
                """
id: CN-ROS-2026-A1B2
title: Work item
status: canonical
related_documents: [GL-ROS-2026-FFFF]
""",
            )
            write_artifact(
                root,
                "research/glossary/wrong-name.md",
                """
id: GL-ROS-2026-0002
title: Misnamed
status: accepted
""",
            )
            findings = {(f.path, f.field, f.message) for f in validate(root, check_registries=False)}
            self.assertEqual(
                findings,
                {
                    ("research/concepts/CN-ROS-2026-A1B2--work-item.md", "related_documents", "broken reference 'GL-ROS-2026-FFFF'"),
                    ("research/concepts/CN-ROS-2026-A1B2--work-item.md", "status", "'canonical' is not allowed for CN"),
                    ("research/glossary/wrong-name.md", "id", "filename must start with 'GL-ROS-2026-0002--'"),
                },
            )
