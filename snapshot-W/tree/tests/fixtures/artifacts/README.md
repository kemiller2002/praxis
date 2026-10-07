# Artifact compatibility fixtures

These repositories were frozen before the F# artifact implementation for
`EX-ROS-2026-A020`. They are language-neutral inputs. `valid-all-kinds`
contains canonical Node/Python registry bytes under `registries/`;
`invalid-mixed` contains independent invalid conditions whose Node findings are
recorded in `manifest.json` after characterization.

Tests must copy a fixture before invoking a writer. No implementation may
rewrite these source fixtures during a test run.
