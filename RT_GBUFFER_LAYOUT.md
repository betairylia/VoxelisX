# RT GBuffer Layout

## Current Render Targets

| Texture | Format | Channels |
| --- | --- | --- |
| `VoxelisX_outColor` / `RenderTarget` | `ARGBFloat` | `RGB`: composed radiance, `A`: hit mask |
| `VoxelisX_outAlbedo` / `AlbedoTarget` | `Default` | `RGB`: primary albedo, `A`: unused/1 |
| `VoxelisX_outNormal` / `NormalTarget` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: 10-bit voxel-face hash stored as exact integer-valued half float, `A`: unused/0 |
| `VoxelisX_NormalDepth` / `DepthTarget` | `RFloat` | `R`: linear eye depth for depth copy |
| `VoxelisX_outMotionVector` / `MotionVectorTarget` | `RGFloat` | `RG`: current UV minus previous UV |

## Temporal History

| Texture | Format | Channels |
| --- | --- | --- |
| `VoxelisX_HistoryIndirectRadiance_*` | `ARGBHalf` | `RGB`: accumulated indirect incident radiance, `A`: validity |
| `VoxelisX_HistoryDepth_*` | `RFloat` | `R`: previous surface view depth |
| `VoxelisX_HistoryNormal_*` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: reserved/0, `A`: validity |

