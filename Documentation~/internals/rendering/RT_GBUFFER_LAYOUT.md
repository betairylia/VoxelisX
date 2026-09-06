# RT GBuffer Layout

## Current Render Targets

| Texture | Format | Channels |
| --- | --- | --- |
| `Caelix_outColor` | `ARGBFloat` | `RGB`: final composed radiance, `A`: hit mask |
| `Caelix_outDirectRadiance` / `DirectRadianceTarget` | `ARGBFloat` | `RGB`: primary emission plus direct radiance, `A`: hit mask |
| `Caelix_outIndirectRadianceRaw` / `IndirectRadianceTarget` | `ARGBHalf` | `RGB`: noisy single-frame indirect incident radiance, `A`: hit mask |
| `Caelix_outIndirectRadianceSpatialTemp` | `ARGBHalf` | `RGB`: horizontal spatial filter intermediate, `A`: hit mask |
| `Caelix_outIndirectRadianceFiltered` | `ARGBHalf` | `RGB`: spatially filtered current-frame indirect incident radiance, `A`: hit mask |
| `Caelix_outAlbedo` / `AlbedoTarget` | `Default` | `RGB`: primary albedo, `A`: unused/1 |
| `Caelix_outNormal` / `NormalTarget` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: 10-bit voxel-face hash stored as exact integer-valued half float, `A`: unused/0 |
| `Caelix_NormalDepth` / `DepthTarget` | `RFloat` | `R`: linear eye depth for depth copy |
| `Caelix_outMotionVector` / `MotionVectorTarget` | `RGFloat` | `RG`: current UV minus previous UV |

## Pass Order

1. `Caelix DXR Trace` writes raw direct radiance, raw indirect radiance, GBuffers, motion vectors, and current depth/normal history inputs.
2. `Caelix Indirect Spatial Filter X` applies a 15-tap horizontal face-hash gated filter.
3. `Caelix Indirect Spatial Filter Y` applies the matching 15-tap vertical filter.
4. `Caelix Indirect Temporal Accumulation` reprojects the previous indirect history with motion vectors, applies depth/normal rejection, and writes the current indirect history.
5. `Caelix Composite` combines `direct + albedo * accumulatedIndirect`.
6. `Caelix Copy To Camera` copies color and depth into the active URP targets.

## Temporal History

| Texture | Format | Channels |
| --- | --- | --- |
| `Caelix_HistoryIndirectRadiance_*` | `ARGBHalf` | `RGB`: accumulated indirect incident radiance, `A`: validity |
| `Caelix_HistoryDepth_*` | `RFloat` | `R`: previous surface view depth |
| `Caelix_HistoryNormal_*` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: reserved/0, `A`: validity |
