from __future__ import annotations

import json
import re
import tempfile
import unittest
from pathlib import Path

from tools.ros_cli import (
    ALLOWED_STATUS,
    ID_RE,
    KIND_CONFIG,
    OPTIONAL_REGISTRY_KINDS,
    build_registries,
    validate,
)

REPOSITORY = Path(__file__).resolve().parent.parent
FSHARP_ARTIFACTS = REPOSITORY / "src" / "Ros.Domain" / "Artifacts"


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


def fsharp_kind_configurations() -> dict[str, tuple[str, str, str, bool]]:
    """The F# ArtifactKinds.configurations records, read from Model.fs."""
    record = re.compile(
        r'Name = "(?P<name>[^"]+)"\s+SourceDirectory = "(?P<source>[^"]+)"\s+'
        r'RegistryPath = "(?P<registry>[^"]+)"\s+IdentifierPrefix = "(?P<prefix>[^"]+)"\s+'
        r"OptionalRegistry = (?P<optional>true|false)"
    )
    text = (FSHARP_ARTIFACTS / "Model.fs").read_text(encoding="utf-8")
    return {
        match["name"]: (match["source"], match["registry"], match["prefix"], match["optional"] == "true")
        for match in record.finditer(text)
    }


def fsharp_allowed_statuses() -> dict[str, set[str]]:
    """The F# ArtifactPolicy.allowedStatuses table, read from Policy.fs."""
    entry = re.compile(r'"(?P<prefix>[A-Z]{2})", Set\.ofList \[(?P<statuses>[^\]]*)\]')
    text = (FSHARP_ARTIFACTS / "Policy.fs").read_text(encoding="utf-8")
    return {match["prefix"]: set(re.findall(r'"([^"]+)"', match["statuses"])) for match in entry.finditer(text)}


def fsharp_identifier_prefixes() -> set[str]:
    text = (FSHARP_ARTIFACTS / "Policy.fs").read_text(encoding="utf-8")
    return set(re.search(r"\^\(\?:\((?P<prefixes>[A-Z|]+)\)-", text)["prefixes"].split("|"))


class OracleAgreementTests(unittest.TestCase):
    def test_kind_configuration_agrees_with_the_fsharp_model(self) -> None:
        fsharp = fsharp_kind_configurations()
        self.assertIn("concepts", fsharp)
        oracle = {
            name: (source, registry, prefix, name in OPTIONAL_REGISTRY_KINDS)
            for name, (source, registry, prefix) in KIND_CONFIG.items()
        }
        self.assertEqual(oracle, fsharp)

    def test_status_and_identifier_rules_agree_with_the_fsharp_policy(self) -> None:
        self.assertEqual(ALLOWED_STATUS, fsharp_allowed_statuses())
        oracle_prefixes = set(re.search(r"\(([A-Z|]+)\)-", ID_RE.pattern)[1].split("|"))
        self.assertEqual(oracle_prefixes, fsharp_identifier_prefixes())
        self.assertTrue({prefix for _, _, prefix in KIND_CONFIG.values()} <= oracle_prefixes)


class ConceptAndGlossaryTests(unittest.TestCase):
    def root(self) -> Path:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root / "registries").mkdir()
        return root

    def write_concept_and_glossary(self, root: Path) -> None:
        write_artifact(
            root,
            "research/concepts/CN-ROS-2026-A001--registry-projection.md",
            """
id: CN-ROS-2026-A001
title: Registry projection
status: accepted
related_documents: [GL-ROS-2026-A002]
""",
        )
        write_artifact(
            root,
            "research/glossary/GL-ROS-2026-A002--canonical-record.md",
            """
id: GL-ROS-2026-A002
title: Canonical record
status: draft
""",
        )

    def test_concept_and_glossary_records_are_registered_and_validated(self) -> None:
        root = self.root()
        self.write_concept_and_glossary(root)
        self.assertEqual(build_registries(root), 0)
        registered = {
            name: [entry["id"] for entry in json.loads((root / "registries" / name).read_text(encoding="utf-8"))]
            for name in ("concepts.json", "glossary.json")
        }
        self.assertEqual(registered, {"concepts.json": ["CN-ROS-2026-A001"], "glossary.json": ["GL-ROS-2026-A002"]})
        self.assertEqual(validate(root), [])
        (root / "registries/glossary.json").unlink()
        self.assertEqual(
            [(finding.path, finding.message) for finding in validate(root)],
            [("registries/glossary.json", "registry is stale; run 'ros registry build'")],
        )

    def test_empty_optional_kinds_need_no_registry(self) -> None:
        root = self.root()
        write_artifact(root, "research/evidence/EV-ROS-2026-A001--e.md", "id: EV-ROS-2026-A001\ntitle: E\nstatus: draft")
        self.assertEqual(build_registries(root), 0)
        written = {path.name for path in (root / "registries").iterdir()}
        self.assertTrue(written.isdisjoint({"requirements.json", "concepts.json", "glossary.json"}), written)
        self.assertEqual(validate(root), [])

    def test_concept_and_glossary_policy_findings(self) -> None:
        root = self.root()
        write_artifact(root, "research/concepts/CN-ROS-2026-B001--bad.md", "id: CN-ROS-2026-B001\ntitle: Bad\nstatus: established")
        write_artifact(root, "research/glossary/wrong-name.md", "id: GL-ROS-2026-B002\ntitle: Wrong name\nstatus: review")
        findings = [(finding.path, finding.field, finding.message) for finding in validate(root, check_registries=False)]
        self.assertEqual(
            findings,
            [
                ("research/concepts/CN-ROS-2026-B001--bad.md", "status", "'established' is not allowed for CN"),
                ("research/glossary/wrong-name.md", "id", "filename must start with 'GL-ROS-2026-B002--'"),
            ],
        )


if __name__ == "__main__":
    unittest.main()
