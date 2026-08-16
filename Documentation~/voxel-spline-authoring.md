# Voxel spline authoring

Create a tool with **GameObject > VoxelisX > Authoring > Voxel Spline**.

1. Select **Edit Spline in Scene View**.
2. Use Unity's spline knot and tangent controls.
3. Choose **Single** or **Parallel Rails**.
4. Set the voxel radius, block color, and emission.
5. Enter Play Mode to generate the voxel entity and its static collision body.

**Pixel Perfect** snaps each path to a one-cell digital line before it applies the radius. Turn it off for a distance-sampled round tube.

Parallel rails use mitered corners. **Corner Miter Limit** changes the largest allowed miter. A sharper corner uses a connected bevel instead.

During Play Mode, select **Keep Changes After Play**. When Play Mode exits, VoxelisX restores the current authoring controls and saves the scene. Select it again if you make more changes after the snapshot.

The working entity is excluded from VoxelisX `.vxw` saves.

- **Bake as New Saveable Entity** creates a regular static `VoxelEntity` and `VoxelBody`. It has no spline controls and is included in the next world save.
- **Bake Into Target Entity** writes the generated blocks into the selected target. Both transforms must use unit scale. Their relative position and rotation must align with the target voxel grid.
