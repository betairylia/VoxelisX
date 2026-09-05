# Inline ray query renderer

A second trace backend for the path-traced renderer. Instead of the DXR pipeline (raygen +
intersection + closest-hit dispatched through a shader table), a compute kernel walks the same
hardware acceleration structure with DXR 1.1 inline ray queries (`RayQuery` / `TraceRayInline`).

Everything downstream is unchanged: the G-buffer contract, the denoise chain and the present stage
are the same. The DXR path is untouched and stays the baseline for performance comparison.

## What actually differs

Only three things, all of them macros the entry file defines before including the shared body
`Runtime/Rendering/Shaders/PathTrace_full/CaelixPathTraceCommon.hlsl`:

| Macro | DXR entry (`Full_raygen.raytrace`) | Compute entry (`RayQuery/CaelixPathTraceRQ.compute`) |
| --- | --- | --- |
| `CAELIX_TRACE_RAY(ray, payload)` | `TraceRay(...)` | `CaelixRayQueryTrace(ray, payload)` |
| `CAELIX_LAUNCH_INDEX` | `DispatchRaysIndex().xy` | the thread's `SV_DispatchThreadID.xy` |
| `CAELIX_LAUNCH_DIM` | `DispatchRaysDimensions().xy` | the `g_LaunchDim` uniform |

`CaelixRayQueryTrace` (in `RayQuery/CaelixRayQueryTrace.hlsl`) replaces both the intersection
shader and the closest-hit shader: it runs the brick DDA
(`CaelixTraceBrickPrimitiveCore`) on every procedural candidate, commits the nearest hit, and fills
the same `RayPayload` the hit group used to fill.

## Pool and instance table

A ray query has no shader table, so there is no per-instance binding. Two global buffers replace it:

* **`CaelixBrickPool`** — the brick records of every sector, held in up to `MaxPages` = 4 raw
  `GraphicsBuffer`s bound as `g_bricks0..3`. Pages exist because one buffer cannot exceed
  `SystemInfo.maxGraphicsBufferSize` (about 3.9 GB) and one record is 1096 bytes, so a few million
  live bricks do not fit one buffer. Each page is capped at `PageCapacityLimitBricks` (serialized on
  the component, clamped to the platform maximum and to a power of two).
* **`CaelixRayQueryInstanceTable`** — a structured buffer bound as `g_Instances`, one 80-byte record
  per RTAS instance, indexed by `InstanceID()`. The record holds the sector's page, its word offset
  inside that page, the previous object-to-world matrix (as four rows, for motion vectors) and the
  hash seed. The slot index *is* the instance ID, passed to `AddInstance(config, matrix, id)`. The
  kernel reads the record once per procedural candidate and switches every brick load on its page.

Sectors own power-of-two ranges inside a page, handed out by a bump pointer with a per-capacity free
list, and hold a `CaelixBrickPool.Handle` — a class, because the pool moves ranges. **Growth
compacts.** When a page has to grow, its live ranges are re-packed contiguously (largest first, so
every range stays aligned to its own size), the free lists are dropped, the page gets a new buffer
and its `Generation` goes up. Contents are not copied, but that costs nothing: a new buffer already
forces every sector on the page to re-upload, and the generation is exactly how a sector notices.
Without compaction, streaming fragments the pool badly enough that it asks for a buffer past the
size cap. Growth targets 1.5x what the page needs rather than doubling; when a page cannot grow any
further the pool opens the next one, and when all four are full it logs an error once and returns an
invalid handle — that sector is then skipped, and it retries every tick.

A sector that only moved (new range, or a compacted page) republishes its instance record but does
**not** rebuild its RTAS instance: the AABBs did not change.

`CaelixRayQueryRenderer.Tick` therefore runs its GPU sync in two loops: pass 2a settles every
sector's pool range (which can grow and compact a page), pass 2b uploads bricks and updates the
acceleration structure. Doing both in one loop would upload into a buffer a later sector then
replaces.

## DXR on the pool

The two backends differ in two independent ways: the dispatch model (shader table vs. one compute
kernel) and the brick storage (a buffer per sector vs. the shared pool). `CaelixRenderer.brickStorage`
separates them: set it to `SharedPool` and the DXR path stores its bricks in the same
`CaelixBrickPool`, so a DXR-vs-ray-query comparison measures only the dispatch model. `PerSector` is
the default and is unchanged.

In pool mode `CaelixRenderer` runs a private instance of `brickMat` with the `CAELIX_BRICK_POOL`
keyword enabled (the asset on disk is never touched). There is no page switch in the hit group: the
DXR path has a per-instance binding anyway, so each sector's property block binds `g_bricks` to the
POOL PAGE holding the sector and carries `_BrickBase`, the word offset of its first brick, which the
intersection shader adds to `CaelixBrickBase(PrimitiveIndex())`. A sector whose range moves
republishes that property block without rebuilding its RTAS instance, exactly as the ray query path
republishes its instance record. (A 4-way page switch inside the intersection shader was tried
first and measured no faster than a direct binding.)

**Page size matters here.** The hit group reads `g_bricks` through a buffer VIEW from its shader
record, and D3D12 caps a buffer view at 2^27 elements: 512 MB for a raw buffer. Every brick past
that mark in a larger page reads as zero, i.e. as empty space, which shows up as sky leaking
through walls and, because the rays then travel further, as a slower frame. The compute kernel
binds its pages as root descriptors and is not affected. So `CaelixRenderer.pageCapacityLimitBricks`
defaults to 2^18 bricks (287 MB, `CaelixBrickPool.DefaultDxrPageCapacityLimitBricks`) while the ray
query renderer keeps 2^21. The pool allows `MaxPages` (32) pages; only the compute kernel is limited
to the `MaxNamedPages` (4) it can switch over, and `CaelixRayQueryRenderer` logs an error when the
pool opens more.

## Readiness

`CaelixGBufferPass.IsReady` demands `CaelixRayQueryRenderer.HasResources` for this backend. The
component only owns its pool, instance table, material buffer and acceleration structure between
`Awake`/`Tick` and `OnDisable`, so the check is false in edit mode (a Scene view camera, where
`Awake` never ran) and while the component is disabled. Without it the pass dispatches against null
buffers and Unity logs `Property (g_Materials) at kernel index (0) is not set` every frame.

## Material table

`Assets/Caelix/VoxelMaterials.hlsl` (generated by Titania) holds its material tables as `static`
arrays, about 150 KB of immediate constant data. The DXR pipeline accepts that; a compute pipeline
does not. With the tables in the trace kernel, D3D12 fails `CreateComputePipelineState` with
`8007000e` (E_OUTOFMEMORY). **That failure is logged only in the Editor log
(`Logs/Editor.log`), not in the console, and the dispatch is silently skipped** — the symptom is a
frame with no Caelix output at all (URP sky only) while `HasKernel`/`IsSupported` still say yes.

So the compute kernel never touches the static tables. `CaelixPathTraceRQ.compute` redefines
`GET_MATERIAL(id)` to read `g_Materials[id & 0xFFFF]`, a structured buffer with one `VoxelMaterial`
per 16-bit block ID (`CaelixRayQueryRenderer.MaterialTable`, 65536 x 40 bytes). Three small
`CaelixBakeMaterials*` kernels fill it once from the static tables, one group of fields each:
copying whole structs keeps the whole array as one immediate constant and fails the same way,
while per-field reads let DXC split the table into per-field arrays that each fit the 64 KB
(4096 float4) immediate-constant limit. `CaelixGBufferPass` records the bake before the first
trace of every new material buffer (`CaelixRayQueryRenderer.MaterialsBaked`).

## Scene setup

1. Add a `CaelixRayQueryRenderer` component to the scene and assign its `brickMat`
   (`Caelix/BrickRTTest`). The ray query path never runs that material's hit group, but
   `RayTracingAABBsInstanceConfig` requires a material.
2. Wire the component into `CaelixHost.rayQueryRenderer`.
3. Disable the `CaelixRenderer` component and enable the `CaelixRayQueryRenderer` one.
4. On the URP renderer asset's Caelix feature: set `backend` to `InlineRayQuery` and assign
   `RayQuery/CaelixPathTraceRQ.compute` to `rayQueryTracer`.

To go back to DXR, reverse steps 3 and 4.

## One scene renderer at a time

Enable exactly one of `CaelixRenderer` and `CaelixRayQueryRenderer`. Both read the same
`ClientWorld`, and two pieces of that state are single-consumer:

* `EntityView.SectorsToRemove` is a queue its consumer **drains**, so whichever renderer ticks first
  takes the removals and the other one leaks acceleration-structure instances and pool ranges.
* `EntityView.ShouldResetMotionVectors` is a flag its consumer **clears** after its per-view loop, so
  the second renderer never sees the frame an entity settled and keeps reprojecting it.

The renderer feature's `backend` only chooses which renderer the G-buffer stage reads; it does not
disable the other component.

## Notes

* The kernel writes 9 UAVs. That is fine on D3D12, but the D3D11 variant Unity also compiles logs
  "more than the 8 maximum currently supported on D3D11.0" as an error at import. Harmless here;
  the backend is D3D12-only anyway (inline ray tracing needs it).
* `CaelixPathTraceRQ.compute` declares `#pragma require inlineraytracing Int64`. `Int64` is there
  because the brick DDA loads the 64-bit micro-occupancy word in one go. If an editor ever rejects
  `Int64` as an unknown feature, drop it from that line rather than changing the DDA.
* `CaelixBrickTrace.hlsl` guards its DXR wrapper behind `#ifndef CAELIX_INLINE_RAY_QUERY`, so the
  compute translation unit never sees a DXR intrinsic. The DDA core itself takes its ray as
  parameters and is shared byte-for-byte by both backends.
* The compute kernel is `CaelixPathTraceKernel`, dispatched at 8x8 threads per group; threads past
  `g_LaunchDim` return immediately.
