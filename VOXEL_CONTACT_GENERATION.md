<title>Voxel Contact Generation</title>

# Voxel contact generation

Design, derivations and history for voxel-vs-voxel narrowphase contact generation.

Code: `Voxelis.Unity.Physics/Unity.Physics/Collision/Queries/VoxelCollisionManifold.cs`
and `PhysicsInfo` in `VoxelisX/Runtime/Core/Block.cs`.

The source files carry only comments that describe what the code does now. Everything
about *why*, what was tried, and what was rejected lives here.

---

## 1. The collision model

Every occupied voxel center carries a **sphere of radius 0.5**. Occupied voxel centers
form a finite **cubical complex**: two adjacent centers span a segment, four span a
square, eight span a cube. The collision surface is that complex swept by the sphere.

Consequences:

* An isolated voxel is a sphere. A one-wide pole is a capsule.
* A square or cube gives an exact flat patch.
* An L shape keeps its two segments and fabricates no diagonal square.
* Every cell is finite, so ownership of a shared boundary clamps to a shared edge or
  point instead of creating an infinite plane at a footprint threshold.

**Do not replace this with box or support-radius contacts.** That was tried once.
Rotated bodies jammed in snug spaces and wedged into each other. The rounding is
deliberate: it keeps the metric rotation-invariant, so a 1-wide pole spins and rolls
freely inside a 1-wide hole. Single voxels and poles rolling like balls and cylinders is
by design, not a bug.

Cell notation used throughout: a cell is `(r, m)` where `r` is a voxel coordinate (the
root) and `m` is an axis mask (X=1, Y=2, Z=4). The cell spans `[r, r+m]` in voxel-center
space. `dim` is the popcount of `m`: 0 for a point, 1 for a segment, 2 for a square,
3 for a cube. A cell exists iff all of its voxels are occupied.

---

## 2. Active cells

`PhysicsInfo.data` holds one bit per cell **rooted at this voxel** that is
**collision-active**. Bits 0-6 keep the positive octet order (+X, +Y, +Z, +XY, +XZ, +YZ,
+XYZ); bit 7 is the bare point.

```
(r, m) is active  <=>  for every axis a not in m:
                         NOT ( (r, m+a) exists AND (r-e_a, m+a) exists )
```

A cell that can grow along axis `a` gives every direction with a positive `+a` component
to the grown cell rooted here, and every `-a` direction to the one rooted a voxel back.
Both together leave only a zero-area slice, so the cell is dropped.

Equivalent per-dimension form (verified identical):

* A vertex is active iff every axis has at least one empty face neighbor.
* An edge along `a` is active iff on each transverse axis at least one of the two
  containing faces is missing.
* A face is active iff at least one of its two cube cofaces is missing.

Effects: flat subdivision edges and vertices vanish; rims, creases, corners, wire ends
and sheet faces stay.

**Coverage invariant** — the theorem that makes this safe: for any point **outside** the
solid region, the distance to the active cells equals the distance to the full complex,
because every inactive cell is contained in a cell that is either active or buried in
solid. Interior distance is **not** preserved; containment needs the volume path.

Bit 6, the cube, is a **volume** cell. It is set whenever the cube exists and is excluded
from every surface path. `IsInterior` means "no active surface cell", which for the first
time describes an actual solid interior.

A solid box's minimum corner sets all 8 bits: three boundary faces, three convex edges,
the point and the cube. There is no "at most three cells per root" bound.

### 2.1 Why AND and not OR

This rule replaced an earlier **containment dedup** which used OR: drop a cell when
*either* grown cell exists. Activity drops it only when *both* do. One operator per axis.

OR is wrong because of the pair set (section 3). At a sheet rim, an edge has a face on
one side only. OR drops the rim edge and keeps the face. The face cannot pair with a
source edge — edge-face is not dispatched — so the edge-edge contact that catches a
crossing rim disappears. A duplicate becomes a hole.

The general rule this illustrates: **a filter may only reject a cell whose dominator is
itself usable in a permitted pair.**

### 2.2 What this superseded

An earlier design used a `normalBin` constraint mask: raw center delta, per-axis exposure
masking, and rank/bin classification. It is entirely gone. `VoxelContact.normalBin` is
vestigial and always 0. Recover it from git history if ever needed.

That scheme failed in two documented ways:

* **Inside-corner phantom launch** (fixed 2026-07, physics repo `617fe39`). Unbounded
  masking zeroed delta components with no transverse footprint check. An A voxel offset
  `(eps, +-1, +-1)` from a B cell with both offset faces unexposed collapsed the masked
  delta to `(eps, 0, 0)`, giving `d = eps - 1 ~ -1`, a phantom deep contact, a
  max-depenetration-velocity launch and a friction-cap spin. Log fingerprint:
  `d = -0.99990 = k_VoxelSignDeadzone(1e-4) - 1`, random timing, random sign.
* **Torn exposure cache.** The exposure byte was neighbor-relative. If narrowphase ran
  while it was half-applied across bricks, a flat top cell momentarily carried a wall
  signature, the vertical component got masked away, and a flat rest was reported as a
  0.5-0.8 deep contact with a sideways normal. Diagnosed with a break-voxel-then-fill
  repro: the same voxel pair flipped from `(n = +-Z, d = -0.79)` on the edit frame to
  `(n = +Y, d = 0)` the next frame with no motion, proving the penetration was
  fabricated. The fix was tick ordering, not masking — see section 8.

---

## 3. The permitted pair set

Only four unordered core-feature pairs are dispatched:

| pair | dim sum | dispatched |
|---|---|---|
| vertex-vertex | 0 | yes |
| vertex-edge | 1 | yes |
| vertex-face | 2 | yes |
| edge-edge | 2 | yes |
| edge-face | 3 | no |
| face-face | 4 | no |
| anything with a cube | 3+ | no |

The rule is exactly `dim(a) + dim(b) <= 2`.

Face-face and edge-face are omitted because for two overlapping finite patches, every
corner of the overlap region is either a vertex of one patch lying inside the other, or a
crossing of two boundary edges. Both of those are in the dispatched set.

**That restriction, not any direction test, is what collapses a flat rest.** A 10x10 slab
on a 12x12 floor went from 67 raw contacts to 16, at 4 distinct points — the slab corners.
Only the 4 corner roots carry an active point; a non-corner rim voxel has both rim
neighbors, so its point is absorbed by the collinear edges. Rim edges can only pair with
the floor's rim edges, which are a voxel out of reach.

### 3.1 Consequence: every pair has a vertex, except edge-edge

This is the fact the whole loop structure rests on. Reading the table: every dispatched
pair has a vertex on at least one side, except edge-edge. So contact generation splits
cleanly into exactly two queries:

* **Vertex query** — source is an active vertex, targets are all cells. Covers
  vertex-vertex, vertex-edge and vertex-face.
* **Edge-edge query** — source is an active edge, targets are active edges.

Faces are never sources. A face only ever meets a vertex, and that vertex is the source
in its own body's vertex query.

---

## 4. Maximality

If cell `c` is contained in cell `d`, then for every pose and every opposing feature `S`,
`dist(S, d) <= dist(S, c)`. So the constraint from `(S, d)` implies the constraint from
`(S, c)` and the smaller pair is pure duplication — **but only when `(S, d)` is actually
emitted.**

The dispatch rule caps the sum at 2, so a feature of dimension `k` leaves the other side
a **budget** of `2 - k`. Only containments inside that budget may drop anything.

> **Rule.** Keep pair `(i, j)` iff `dim i + dim j <= 2`, and `i` is maximal among cells of
> dimension `<= 2 - dim j`, and `j` is maximal among cells of dimension `<= 2 - dim i`.

This is exact. The permitted set is closed downwards in the product order, so its maximal
elements are precisely the pairs the two-sided test keeps. Proof is the three grow-cases:
grow `i` only, grow `j` only, grow both — each contradicts one of the two conditions.

`PhysicsInfo.MaximalFeatureMask(budget)` implements the per-root half:

| budget | kept |
|---|---|
| 0 | the point, if active — nothing of dimension 0 contains it |
| 1 | active segments; the point only when no segment covers it |
| 2 | active squares; a segment only when no active square spans its axis; the point only when no segment or square is rooted here |

The square-to-segment coverage is a packed nibble table, `k_EdgesCoveredByFaces =
0x77767530`: index is the three face bits in `BitFaceXY` order, value is the segments they
cover.

**The cube is never a dominator.** It emits no contact, so a square contained only in a
cube must survive. Same reason `CoverMaskForAxes` excludes it.

### 4.1 Maximality is a property of (root, budget)

This killed the idea of caching a "maximal" byte in `PhysicsInfo`.

The end voxel of a one-wide bar roots a point covered by a segment. At budget 1 the point
must go. At budget 0 — against a target face — it must stay, because the segment cannot be
paired at all there. A single stored mask would delete the vertex-face contact at every
bar end. So the flag would have been wrong, not merely wasteful, quite apart from costing
another ~800 MiB in the test scene.

The mask is a pure function of the byte already stored, so it is computed, never stored.

### 4.2 Why same-root maximality needs no further argument

Both cells of a dominating pair come from the same `PhysicsInfo` byte, so the dominating
pair is always enumerated in the same loop. No probe range, pass order or dedup rule can
take it away.

**Cross-root** domination is different. Dominators of all cells at `r` live in the 8 bytes
of the negative octant, and the 4 bytes `{r, r-ex, r-ey, r-ez}` catch everything except
three diagonal-root faces for the point bit. It needs the dominator root to actually be
probed. That checks out — the probe window already extends a voxel to the negative side,
and the pass that skips a key target is the one whose partner pass emitted that pair — but
it is unproven and is not implemented.

Measured effect of same-root maximality alone: solid box minimum corner against a vertex
source goes from 7 pairs to 3. A flat rest is unchanged, because its duplicates are
cross-root.

### 4.3 Trap: the pair loop is symmetric

The loop is a cross product of both roots' features. There is no "the source contributes
only vertices and edges" restriction anywhere in it. An edge source legitimately meets a
target **vertex** (dim sum 1) — for instance an isolated voxel, whose point is maximal at
budget 1. Implementing the target set as "edges only when the source is an edge" would
delete that contact, because the key-key skip means the reverse pass never re-emits it.

---

## 5. The window theorem

Work in voxel-coordinate space, where the voxel with integer coordinate `c` has center
`c + 0.5`. Let `S` be the source feature in target grid space, with axis-aligned bounds
`[s_lo, s_hi]`. Let `D = 1 + margin` be the core reach: two radii of 0.5 plus the
speculative margin.

Choose a window of target voxels `W = product over a of [lo_a, hi_a]`. Let `W_box` be the
box spanned by the window's voxel **centers**. Enumerate exactly the cells whose voxels
all lie in `W`.

**Clipping lemma.** For a convex axis-aligned cell `C` and a box `B` with `S` inside `B`,
if `C` meets `B` then the nearest point of `C` to `S` lies in `C ∩ B`. Distance is
separable per axis, and on each axis the nearest point of an interval overlapping `B`'s
interval lies in the overlap.

**Correctness condition.** Every cell within `D` must meet `W_box`. A cell that misses
`W_box` is disjoint from it on some axis, so its distance there is at least the gap to
`W_box`'s face. Requiring that gap to exceed `D` gives, per axis:

```
lo_a < s_lo_a - D + 1          hi_a > s_hi_a + D - 1
```

With `W_box` endpoints at voxel centers `lo_a + 0.5` and `hi_a + 0.5`:

```
lo_a = ceil(s_lo_a - D + 0.5) - 1
hi_a = floor(s_hi_a + D - 1.5) + 1
```

For a **point** source at `p`, writing `v = floor(p - 0.5)` and `f = p - v - 0.5` in
`[0,1)`, this reduces to the **bracket rule**:

```
lo_a = v_a + (f_a >  margin      ? 0 : -1)
hi_a = v_a + (f_a <  1 - margin  ? 1 :  2)
```

so 2, 3 or 4 voxels per axis. At `margin = 0` it is exactly the 2x2x2 bracket, whose
8 voxels determine the 27 cells of one dual cube: 8 vertices, 12 edges, 6 faces, 1 cube.

### 5.1 Reading the theorem

* At `margin = 0`, a cell with no bracket voxel is at distance **at least 1**, i.e.
  contact distance at least 0. So the bracket is exact for touching and penetration and
  gives up only separated pairs.
* The bound is tight. With centers on the integers and bracket `{0,1}^3`, the edge from
  `(-1,0,0)` to `(-1,1,0)` and the point `p = (0, 0.5, 0)` are at distance exactly 1 with
  no bracket voxel.
* The **sphere crosses the bracket boundary** and that is fine. The bracket bounds the
  cell **cores**, not the swept surfaces; both radii are already folded into the
  threshold of 1. With `p = (0.1, 0.5, 0.5)` the sphere reaches `x = -0.4`, but the
  nearest voxel center outside the bracket is at distance 1.31 — the sphere pokes into
  empty dual space.
* The "1 cube" is the **dual** cube. Its corners are voxel centers, not voxel corners; it
  is offset half a voxel from the block grid. It is the volume cell, so all 8 bracket
  voxels occupied means `p` is strictly inside the solid.

### 5.2 The rest case sits exactly on the boundary

A slab voxel resting on a floor voxel has centers exactly 1.0 apart. With centers at
`coord + 0.5`, a slab center at `z = 1.5` gives `v_z = floor(1.0) = 1` and `f_z = 0`. The
bracket is `z in {1,2}` — the slab's own layer and the empty space above. The floor is
**not** in it, and the distance is exactly 1, the equality case.

Penetrate slightly and the bracket finds the floor. Separate slightly and it does not. So
the naive bracket flickers exactly where the solver parks a resting body.

**The per-axis widening is therefore mandatory, not an optimization.** At rest `f_z = 0`
triggers `lo_z = v_z - 1`, giving 3x2x2 = 12 voxels instead of 8.

---

## 6. Closed forms — no GJK

Target cells are axis-aligned boxes in target grid space. The dispatched pairs need only
three routines:

| pair | routine |
|---|---|
| source vertex vs target point / edge / face | clamp the point into the target box |
| source edge vs target point | transform the target point into source space, clamp into the source segment |
| source edge vs target edge | clamped segment-segment |

All three are closed form. Nothing calls a general convex-distance query.

Two constants fall out of the geometry and are hoisted per body pair:

* Every source edge along axis `a` has direction `R.c_a` in target space — the same vector
  for every source voxel. No per-source direction transform, ever.
* Every target edge is exactly `e_x`, `e_y` or `e_z`.

So edge-edge has only 9 direction combinations, and their cross products and denominators
are constant for the whole pair for the whole tick.

---

## 7. Carrier dedup

A witness point on a shared boundary is reported once per cell that touches it. At an
exactly aligned flat rest, a source vertex sits above the shared corner of four floor
tiles, all four faces are at distance exactly 1, and all four report the **same witness
and the same normal**.

Rather than assign ownership in advance, compute the witness and then canonicalize it:
snap it to the lowest-dimensional cell of the complex that contains it — its **carrier** —
and dedup on `(carrier root, carrier mask, quantized normal)`. Per axis, a witness
coordinate that lands on a voxel-center plane means the carrier does not span that axis.

This is exact integer dedup, and it replaces the old float position-and-normal tail scan.

### 7.1 Why not exclusive ownership

`VOXEL_CONTACT_FEATURE_TOPOLOGY_PROPOSAL.md` made ownership an exclusive half-open
partition and a validity gate. In a voxel world, normals are almost always exactly
axis-aligned, i.e. exactly on the partition boundary. With priority "face > edge >
vertex", a slab's bottom face owns `-Y`, so the corner vertex fails the gate and the four
corner contacts it predicts are deleted. Closed, non-exclusive cones or a domination test
avoid this. Canonicalizing the witness avoids it entirely, because it never asks a feature
to prove it owns a direction.

The older `k_EnableSeamOwnership` half-open scheme is gone for the same reason, plus two
of its own: active cells at one root overlap, so "the anchor roots a covering cell" is not
the same question as "the anchor can emit this contact"; and a covering cell forming no
permitted pair with the source would claim a point it cannot report, turning a duplicate
into a hole.

---

## 8. Invariants the rest of the engine must hold

* **`PhysicsInfo` must be refreshed before narrowphase and be coherent with live blocks.**
  Tick order inside `VoxelisXWorld.Tick` is: `ApplySnapshot`, then the dirty-propagation
  block, then mass recompute, then `SimulateStep`, then copy back, then renderer. If
  propagation runs after physics, neighbor bricks of an edit never get `RequireUpdate`,
  `RefreshPhysicsSlot` skips them, and narrowphase reads a cache incoherent with live
  blocks. That produced the phantom deep contacts and launches described in section 2.2.
* Consumers must read `*RequireUpdateFlags`, never raw `*DirtyFlags`, which are cleared
  every tick.
* Uniform body scale is assumed to be 1.

---

## 9. Known gaps

* **Containment and deep overlap are not covered.** Cube cells take part in no permitted
  pair, so a body buried more than a voxel inside another finds no nearby surface feature
  and generates nothing. `BitCube` is stored for a future volume path. The window
  formulation detects the case for free: all 8 bracket voxels occupied means the source
  vertex is strictly inside the solid, and the nearest bracket face gives a direction.
* **No merging or reduction.** Contacts go raw into a flat list, one single-point manifold
  and one event per raw contact.
* **No normal-cone gate.** See section 10.
* **Cross-root maximality is not implemented.** See section 4.2.

---

## 10. Normal cones — analysed, deliberately not implemented

A normal cone is the set of outward directions for which a feature is the closest part of
the body: a face has 2 sides, an edge a 90 degree sector, a vertex an octant; 26 bits
total. It is a **redundancy** filter, not a validity gate.

Two failure classes it would fix:

1. Speculative linearization — a buried convex rim reports a tilted plane constraint that
   drags a body sliding past it.
2. Warm-start churn from marginal contacts flickering across `maxDistance`.

It does **not** fix nested-at-one-root duplicates. Only the pair restriction and the
reducer do that.

If it is ever wanted, **do not build or store a 26-bit mask.** Evaluate it lazily at the
witness: take the support set of the witness point — 1 voxel for a vertex witness, 2 for
an edge, 4 for a face — shift it by `sign(n_a) * e_a` for each axis where `n_a` is
meaningfully non-zero, and reject when every shifted voxel is occupied. That is 1 to 8
occupancy bit reads and no new storage. Same rule as the static classification, with the
direction supplied instead of quantified.

**Trap:** it must only reject a pair whose dominator is itself a permitted pair, or a
duplicate becomes a hole. Safe rule: enable it for vertex sources only, where the
replacement is vertex-vertex, vertex-edge or vertex-face, all permitted. For edge sources,
either skip it or allow edge-face.

---

## 11. Why extra contacts are safe, and when they are not

The body is a union of convex cells, so non-penetration is the conjunction of one
condition per cell pair. Every extra pair contact is a condition the true geometry already
imposes, and it cannot forbid a legal pose. **Redundancy is a numerics problem, not a
correctness one**: speculative drag, warm-start churn, N-times depenetration, friction
bases fighting each other, direct-solver conditioning.

That last one matters. The direct solver assembles a constrained system, and redundant
duplicate rows can make it ill-conditioned. If direct-solver bodies jitter or go stiff in
snug contact, suspect duplicate contacts before suspecting the solver settings.

---

## 12. The merging stage — agreed direction, not implemented

The old fusion was removed in 2026-07. It was body-order dependent: the same-A-voxel
requirement caught `wall | A | wall` but not the `A | B | A` ring-on-pole case. Its
replacement must be pair-symmetric.

Agreed direction: per-(pair, axis) patch-level equality extraction. Opposing snug contact
point sets fuse iff the transverse convex hulls of the contact **points** intersect —
points, meaning sphere anchors, not faces, which is what preserves the rounding so a 1x1
peg spins and a 2x2 does not.

Hull intersection is the **gate** only; it is equivalent to a self-stress or non-negative
zero-combination existing for that axis. Rank and anchors come from the **self-stress
support**, not from the extent of the intersection `K`. The slab-in-channel case proves
it: a bar above at `x = 3` and bars below at `x = 1, 5` gives `K = {3}`, a single point,
but rank 2 — rotation is equality-locked by the three-point clamp couple. Extent-derived
anchors would under-extract.

Rule: if `K` meets the relative interior of a side's hull, all of that side's points
participate. If `K` lies on a boundary face of the hull, only that face's points
participate. Staircase case `T+ = {1,5}`, `T- = {5}`: `K` sits at a hull vertex, so rank
is 1 and the lone bar stays unilateral — no fabricated weld. Anchors are the at most 4
lateral extremes of the participating set; rank is `1 + affine-dim(participating spread)`;
consumption is span membership. On-axis anchoring keeps spin free.

The solver-side bilateral machinery is fully removed (physics repo `848afac` reverts the
solver and event side of `5d5c062`): no `JacobianFlags.IsBilateral`, no bilateral branches
in `ContactJacobian`, no event `FlagBilateral`. Recover `5d5c062` for the old reference
implementation — impulse-clamp skip, symmetric pull-speed clamp, keep-solving-while-
separated, absolute-impulse friction budget, restitution 0 — if the merging algorithm
wants any of it back.

---

## 13. Profiling

Generation carries funnel counters (`VoxelContactCounters`, collected by
`VoxelContactProfiler`). They are compiled out unless the scripting define
**`VOXELIS_CONTACT_PROFILING`** is set, so a normal build pays nothing: with no reader, the
per-root increments are dead stores that Burst removes.

To measure:

1. Add `VOXELIS_CONTACT_PROFILING` under Project Settings > Player > Scripting Define Symbols.
2. Tick `enableContactProfiling` on `VoxelisXPhysicsWorld`. Set `contactProfilingLogInterval`
   (default 60 steps) and `contactProfilingAverage`.
3. Read the per-interval report in the console.

Counters accumulate on the stack for one body pair and flush once, so the hot loops never
touch shared memory; the flush is one interlocked add per field per pair. Without the define
the toggle warns once rather than reporting an all-zero funnel as if it were data.

The funnel narrows in stages:

```
body pairs -> source features -> window roots -> occupied -> active -> cell tests -> contacts
```

Three ratios are the ones worth acting on:

| ratio | what it decides |
|---|---|
| `roots / contact` | how much of each window is swept for nothing. Large means the target loop is search-bound and worth restructuring into a gathered local bit window with a branch-free kernel. |
| `cache hit` | whether sector hash lookups still cost anything after the brick cache. Low means a per-source-brick gathered window would pay for itself. |
| `dedup share` | how much redundancy carrier canonicalization is absorbing. High means the seam is still the dominant duplicate source and a reducer would help the solver. |

`occupied share` and `active share` say how much of the window is empty space versus solid
interior, which separates "the window is too big" from "the window is right but the body is
mostly interior".

If `roots / contact` is small and the cache hit rate is high, the loop is near its floor and
what remains is arithmetic, not search — at which point the SoA and branch-free kernel work
is the next step, not a wider cull.

## 14. Verification

* `Claude/verify_physics_active.py` — cross-checks the activity bit-row algebra against
  both the single-axis definition and the independent per-dimension geometric rules,
  checks the root-local key mask, asserts every read stays inside `[-1, 8]`, and proves
  the exterior-coverage theorem on random sample points.
* `Claude/verify_physics_dedup.py` — superseded containment version, kept for reference.
* `Claude/verify_maximal_features.py` — proves `MaximalFeatureMask` against a brute-force
  containment definition over all 256 bytes, checks every mask stays inside its budget,
  and proves over all 65536 root pairs that the two-sided filter keeps exactly the maximal
  permitted pairs with no dropped pair lacking a kept dominator.
* `VoxelisX/Tests/Editor/VoxelContactManifoldTests.cs` and `VoxelEntityPhysicsTests.cs`.

Compile without Unity:
`dotnet build F:\RP_Games\Titania\voxelis.Unity.Physics.csproj` (also `voxelis.voxelisx`,
`voxelis.voxelisx.tests`).

Run tests: `unity command run_tests --mode editor --filter <name> --async_tests true`,
then `unity command test_status`. Result entry keys are PascalCase (`FullName`, `Status`,
`Message`); the summary keys are lowercase.

Debug aid: `VoxelisXPhysicsWorld.ContactDebug.cs` (`enableContactDebugLogging`) dumps
per-contact normal, distance and body velocities on a linear-speed spike. Events carry
`Distance` and `DebugFlags`.
