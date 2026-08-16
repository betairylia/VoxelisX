# Voxel spline authoring

Create a tool with **GameObject > VoxelisX > Authoring > Voxel Spline**.

1. Select **Edit Spline in Scene View**.
2. Use Unity's spline knot and tangent controls.
3. Choose **Single** or **Parallel Rails**.
4. Set the voxel radius and the block id.
5. Enter Play Mode to generate the voxel entity and its static collision body.

**Block ID** is the raw 16-bit value written into every generated voxel, the same id the world
loaders and the renderer use. Bit 15 (`0x8000`) marks an opaque block, ids `0x0000`–`0x01FF` are the
transparent slots, and `0` is empty. The default is `0x8000`.

**Pixel Perfect** snaps each path to a clean one-cell digital line before it applies the radius: one
cell per major-axis step, with the redundant corner cell of every L-shaped step removed, the same
rule a pixel-art pencil uses. Turn it off for a distance-sampled round tube.

Voxel `(i, j, k)` covers the entity-local box `[i, i+1)` on every axis, so a path point belongs to the
cell that contains it and that cell renders centred half a unit further along each axis. The Scene
view draws the smooth path in blue and, with Pixel Perfect on, the exact cell centres in orange.

Parallel rails offset the sampled path along a rotation-minimizing frame, so the two rails stay on
their own side of the path through turns of any angle and through vertical sections. Corners are
mitered; **Corner Miter Limit** changes the largest allowed miter, and a sharper corner uses a
connected bevel instead.

**Snap Rails To Grid** (on by default) puts both rails exactly on voxel centers. Voxel centers sit on
half-integers, so a rail offset by a whole number of voxels from a grid-aligned spline lands on a
cell boundary and rounds to one side — the pair then sits half a voxel off center, which looks like
one rail pulled in and the other pushed out. Snapping rounds the spacing to the nearest odd number of
voxels, which is the same parity rule that makes a symmetric pixel-art shape around a one-pixel
center line have an odd width. The inspector shows the spacing actually used.

Voxels can only be centred on half-integers, so a spline authored at whole-number local coordinates
generates voxels half a unit away from it on each axis. Author at `x.5` coordinates, or ignore it —
the orange preview line shows exactly where the cells land.

Undo and redo work on the spline, on the tool's settings, and on creating the tool itself. Because
the generated voxels live in native memory that Unity's undo system does not track, an undo restores
the *inputs* and the tool then regenerates the voxels from them.

During Play Mode, select **Keep Changes After Play**. That marks the tool; VoxelisX reads its live
values as Play Mode exits, applies them to the scene object and saves the scene, so edits made after
you pressed the button are included too. Select **Cancel Keep Changes** to drop the mark.

The working entity is excluded from VoxelisX `.vxw` saves.

- **Bake as New Saveable Entity** creates a regular static `VoxelEntity` and `VoxelBody`. It has no
  spline controls and is included in the next world save.
- **Bake Into Target Entity** writes the generated blocks into the selected target. Both transforms
  must use unit scale. Their relative position and rotation must align with the target voxel grid.

## Limits

A rebuild stops at 2,000,000 voxels and the radius is capped at 64, so a runaway spline warns instead
of hanging the editor. Non-finite spline points are skipped.
