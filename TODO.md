# TODO

## Rendering

- Handle ray tracing AABB removal/compaction for culled renderer bricks.
  `GenerateSectorRenderDataJob.ProcessBrick` can remove entries from `rendererBrickMap`, but the sector RTAS instance still uses the existing AABB buffer layout. The zero-occupancy brick upload makes stale AABBs shader-miss, but the RTAS and renderer brick map can still contain dead primitive slots. Decide whether to keep tombstoned AABBs intentionally or add a compaction/rebuild strategy.

- Fix queued sector removal in `VoxelisXRenderer`.
  `VoxelisXRenderer.Tick` calls `SectorRenderer.RemoveMe` for queued sector removals, but `RemoveMe` only removes the RTAS instance when `shouldRemove` was previously set by `MarkRemove`. The removal path should either mark before removing or make `RemoveMe` unconditional for queued removals.

- Align mesh fallback invalidation with the dirty/require-update contract.
  `SectorMeshRenderer.ScheduleJobs` scans `brickDirtyFlags`, while world tick clears dirty flags before renderer tick. Mesh rendering should consume propagated `requireUpdate` flags like the ray tracing renderer, and it should invalidate neighboring chunks/sectors when exposed faces cross chunk or sector boundaries.
