#!/usr/bin/env python3
"""EX-ROS-2026-A024 handoff validator.

Mechanically validates a handoff document against handoff.schema.json using a
dependency-free implementation of exactly the JSON Schema keywords that schema
uses (type, required, additionalProperties: false, properties, items, const,
enum, pattern, minLength, uniqueItems). An unsupported keyword is an error, so
a schema change cannot be silently ignored. It then checks repository facts:
completedItem equals --item and headCommit names a commit that is an ancestor
of (or equal to) HEAD.

Usage: python3 validate_handoff.py HANDOFF.json --item PRAXIS-GROUP-0N
Exit 0 when valid; 1 with one finding per line otherwise.
"""
import argparse
import json
import os
import re
import subprocess
import sys

SCHEMA_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "handoff.schema.json")
SUPPORTED = {"$schema", "$id", "title", "type", "required", "additionalProperties", "properties",
             "items", "const", "enum", "pattern", "minLength", "uniqueItems"}
TYPES = {
    "object": lambda v: isinstance(v, dict),
    "array": lambda v: isinstance(v, list),
    "string": lambda v: isinstance(v, str),
}


def check(schema, value, where):
    unsupported = sorted(set(schema) - SUPPORTED)
    if unsupported:
        return [f"{where}: schema uses unsupported keyword(s) {unsupported}"]
    type_name = schema.get("type")
    if type_name and not TYPES[type_name](value):
        return [f"{where}: expected {type_name}"]
    findings = []
    findings += [f"{where}: must equal {schema['const']!r}"] if "const" in schema and value != schema["const"] else []
    findings += [f"{where}: must be one of {schema['enum']}"] if "enum" in schema and value not in schema["enum"] else []
    if isinstance(value, str):
        findings += [f"{where}: shorter than {schema['minLength']}"] if len(value) < schema.get("minLength", 0) else []
        findings += [f"{where}: does not match {schema['pattern']}"] if "pattern" in schema and not re.search(schema["pattern"], value) else []
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        findings += [f"{where}: missing required {key!r}" for key in schema.get("required", []) if key not in value]
        findings += [f"{where}: unexpected property {key!r}" for key in value
                     if schema.get("additionalProperties") is False and key not in properties]
        findings += [f for key, sub in properties.items() if key in value for f in check(sub, value[key], f"{where}.{key}")]
    if isinstance(value, list):
        canonical = [json.dumps(item, sort_keys=True) for item in value]
        findings += [f"{where}: items are not unique"] if schema.get("uniqueItems") and len(set(canonical)) != len(canonical) else []
        findings += [f for index, item in enumerate(value) if "items" in schema for f in check(schema["items"], item, f"{where}[{index}]")]
    return findings


def repository_facts(document, item):
    findings = [f"completedItem is {document.get('completedItem')!r}, expected {item!r}"] if document.get("completedItem") != item else []
    head = document.get("headCommit")
    if isinstance(head, str) and re.fullmatch(r"[0-9a-f]{40}", head):
        exists = subprocess.run(["git", "cat-file", "-e", f"{head}^{{commit}}"], capture_output=True).returncode == 0
        ancestor = exists and subprocess.run(["git", "merge-base", "--is-ancestor", head, "HEAD"], capture_output=True).returncode == 0
        findings += [] if exists else [f"headCommit {head} is not a commit in this repository"]
        findings += [] if not exists or ancestor else [f"headCommit {head} is not an ancestor of HEAD"]
    return findings


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("handoff")
    parser.add_argument("--item", required=True)
    options = parser.parse_args()
    with open(SCHEMA_PATH, encoding="utf-8") as handle:
        schema = json.load(handle)
    try:
        with open(options.handoff, encoding="utf-8") as handle:
            document = json.load(handle)
    except (OSError, ValueError) as error:
        print(f"$: unreadable JSON: {error}")
        return 1
    findings = check(schema, document, "$") + (repository_facts(document, options.item) if isinstance(document, dict) else [])
    print("\n".join(findings) if findings else f"valid: {options.handoff}")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
