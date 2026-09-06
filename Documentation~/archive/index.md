# Historical documents and relocation record

Reorganized on 2026-09-06 from Caelix `253d632`. Existing bodies and evidence were
retained. The eight former top-level Markdown paths now forward to their new
locations. Original metadata sidecars moved with the associated documents.

## Historical design

These reports explain earlier reasoning. Proposed signatures, measurements, and
implementation plans are not a statement of today's supported API.

| Document | Context | Current starting point |
|---|---|---|
| [ECS versus non-ECS](design/ECS_vs_NonECS_Decision.md) | Decision report dated 2025-11-02, with research and recommendations from that period. | [Server/client usage](../manual/server-and-client.md) |
| [Alien entity-aware reads](design/ALIEN_ENTITY_AWARE_VOXEL_PLAN.md) | Historical proposal; parts evolved into Core's neighborhood reader. | [Automata](../manual/automata.md) |
| [Alien dirty propagation](design/ALIEN_DIRTY_PROPAGATION_PLAN.md) | Historical plan; current orchestration uses post-physics overlap propagation. | [Tick and dirty propagation](../internals/tick-and-dirty.md) |

## Dated validation

[Local Host R&D readiness](validation/RND_VALIDATION.md) records its 2026-09-06
code revisions, environment, executed tests, scale measurements, and limitations.
Its two JSON artifacts are retained beside it under `validation/ValidationResults`.
Those tests were not rerun as part of this documentation reorganization.

The reusable [validation script](../Invoke-RndValidation.ps1) remains at its
original path so recorded commands continue to work. The propagation-mask
[generator](../generate_propagation_masks.py) also remains in place.

## Existing engineering pages

These pages remain useful engineering references and have been filed by topic.
Their detailed rendering claims and setup instructions were not revalidated in
this pass. Read each page's own dates and scope, and verify relevant code before
changing a subsystem.

| Former filename | New location |
|---|---|
| `SERVER_CLIENT_ARCHITECTURE.md` | [Internals](../internals/SERVER_CLIENT_ARCHITECTURE.md) |
| `BUDGET_RENDERER.md` | [Rendering internals](../internals/rendering/BUDGET_RENDERER.md) |
| `RT_GBUFFER_LAYOUT.md` | [Rendering internals](../internals/rendering/RT_GBUFFER_LAYOUT.md) |
| `voxel-spline-authoring.md` | [Authoring manual](../manual/voxel-spline-authoring.md) |

Core had no documentation directory before this change. Its new index and storage
reference link here for cross-package historical context instead of duplicating
the old reports.
