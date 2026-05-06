# RT GBuffer Layout

## Current Render Targets

| Texture | Format | Channels |
| --- | --- | --- |
| `VoxelisX_outColor` | `ARGBFloat` | `RGB`: final composed radiance, `A`: hit mask |
| `VoxelisX_outDirectRadiance` / `DirectRadianceTarget` | `ARGBFloat` | `RGB`: primary emission plus direct radiance, `A`: hit mask |
| `VoxelisX_outIndirectRadianceRaw` / `IndirectRadianceTarget` | `ARGBHalf` | `RGB`: noisy single-frame indirect incident radiance, `A`: hit mask |
| `VoxelisX_outIndirectRadianceSpatialTemp` | `ARGBHalf` | `RGB`: horizontal spatial filter intermediate, `A`: hit mask |
| `VoxelisX_outIndirectRadianceFiltered` | `ARGBHalf` | `RGB`: spatially filtered current-frame indirect incident radiance, `A`: hit mask |
| `VoxelisX_outAlbedo` / `AlbedoTarget` | `Default` | `RGB`: primary albedo, `A`: unused/1 |
| `VoxelisX_outNormal` / `NormalTarget` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: 10-bit voxel-face hash stored as exact integer-valued half float, `A`: unused/0 |
| `VoxelisX_NormalDepth` / `DepthTarget` | `RFloat` | `R`: linear eye depth for depth copy |
| `VoxelisX_outMotionVector` / `MotionVectorTarget` | `RGFloat` | `RG`: current UV minus previous UV |

## Pass Order

1. `VoxelisX DXR Trace` writes raw direct radiance, raw indirect radiance, GBuffers, motion vectors, and current depth/normal history inputs.
2. `VoxelisX Indirect Spatial Filter X` applies a 15-tap horizontal face-hash gated filter.
3. `VoxelisX Indirect Spatial Filter Y` applies the matching 15-tap vertical filter.
4. `VoxelisX Indirect Temporal Accumulation` reprojects the previous indirect history with motion vectors, applies depth/normal rejection, and writes the current indirect history.
5. `VoxelisX Composite` combines `direct + albedo * accumulatedIndirect`.
6. `VoxelisX Copy To Camera` copies color and depth into the active URP targets.

## Temporal History

| Texture | Format | Channels |
| --- | --- | --- |
| `VoxelisX_HistoryIndirectRadiance_*` | `ARGBHalf` | `RGB`: accumulated indirect incident radiance, `A`: validity |
| `VoxelisX_HistoryDepth_*` | `RFloat` | `R`: previous surface view depth |
| `VoxelisX_HistoryNormal_*` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: reserved/0, `A`: validity |
