# Decisions

Record accepted architectural and operating decisions here in compact form.

| Date | Decision | Rationale | Related artifacts |
|---|---|---|---|
| 2026-07-24 | Artifact files are canonical; registries are generated. | Avoid dual-write drift and merge conflicts. | DF-ROS-2026-A001 |
| 2026-07-24 | New IDs use parallel-safe tokens; accepted content is immutable. | Git branches cannot safely share a sequential allocator. | DF-ROS-2026-A002 |
| 2026-07-29 | Package a self-contained additive greenfield bootstrap. | Let beginning projects pin and install ROS without source-repository access. | DF-ROS-2026-A003 (review) |
