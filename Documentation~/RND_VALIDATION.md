# Local Host R&D readiness

Validated on 2026-09-06. The server-authoritative simulation and client replica are
suitable for continued local Host R&D with the lifecycle fixes below. A rewrite
is not justified by the failures found in this review. The supported envelope is
one trusted local client, ordered in-process delivery, main-thread orchestration,
and enough RAM for the complete server, replica, receive queue, and renderer.

## Behavior now covered

| Contract | Executed check |
|---|---|
| Paused edits stay in the channel | Interleaved edit/query/edit/typed-query retains both edits in FIFO order. Initial frozen tick also retains edits. |
| Queries work while paused | Committed voxel and typed queries; actual Host.Update in Play Mode with Time.timeScale = 0. |
| Manual step applies edits once | Tick index advances once, queue drains, and latest ordered edit reaches the replica. |
| Save reload replaces existing GUID contents | Three load/edit/step cycles remove surplus client bricks and sectors; authored component binds back to the replacement. |
| Same ID does not imply same lifetime | World removal/recreation, changed slot mask, ForgetWorld, entity replacement, and sector replacement between ticks. |
| Render jobs release storage safely | Mesh resources and ray-tracing instances retire before sector/world data is disposed. |
| Renderer switching works on quiet data | Rebinding mesh source and ray renderer disable/re-enable rebuild existing geometry without another edit. Ray-tracing acceleration structures were built on actual hardware. |
| Removed neighbors expose geometry | Surviving boundary bricks receive render work without creating replica sectors. |
| Several server ticks may precede a client frame | Replacement and four queued edit ticks converge to exact server Block storage. |
| Large sync/reload fits the tested machine | Byte comparisons of every allocated Block brick in demo.vxw and horizon.vxw after initial sync, queued deltas, and reload. |

The full Editor suite passed **269/269, zero failures, zero skips**. This includes
the coroutine test that enters and exits Play Mode. The ray test ran; it did not
take its unsupported-hardware skip. The final reusable script was exercised for
both the complete suite and a real-save scale run.

## Measured scale

Windows 11, Unity 6000.5.6f1, Intel Core i7-14700KF, 65,291 MB reported system RAM,
RTX 5080. These are individual Editor runs, with other Editors open. Timings are
observations, not performance guarantees. Scale runs exercise simulation and CPU
replication; they do not render the entire large world or measure its GPU memory.

| Measurement | demo.vxw | horizon.vxw |
|---|---:|---:|
| Initial entities | 58 | 1 |
| Initial sectors | 1,031 | 4,696 |
| Initial allocated bricks | 981,606 | 3,123,385 |
| Load + initial sync + exact comparison | 3.59 s | 10.88 s |
| Reload + sync + exact comparison | 3.04 s | 10.25 s |
| Peak receive queue payload | 0.94 GiB | 3.00 GiB |
| Highest sampled process private memory | 9.88 GiB | 21.50 GiB |
| OS peak resident memory | 8.11 GiB | 16.12 GiB |
| Unity allocated memory before / after disposal | 0.51 / 0.56 GiB | 0.52 / 0.56 GiB |

The four-tick edit workload changes 1,000 bricks per tick on an extra static
entity. The scale comparison checks entity counts, sector membership, brick
allocation, and every Block byte. Allocation includes bricks that may now contain
empty voxels. Load replaces entities named in the file and retains unrelated live
entities; the extra edit entity intentionally survives reload.

The queue counters track payload bytes, excluding managed array overhead. Windows
process counters are read through GetProcessMemoryInfo because Unity's Mono
Process.PrivateMemorySize64 returned zero. Private memory is sampled at named
stages; a shorter intermediate peak may be higher. Peak resident memory comes
from the OS counter. Managed and native allocator reservations can remain after
disposal: horizon's private memory ended at 6.24 GiB versus 2.98 GiB before load,
while Unity allocated memory returned close to baseline. This is not a long soak
test or proof that every allocation is leak-free.

Raw measurements are in `ValidationResults/demo-scale.json` and
`ValidationResults/horizon-scale.json`. Original save SHA-256 values:

- demo.vxw: `B59EDFE891D65B4007DBE08485C9D23E1C832707A6776B92D5DA50125A3F3D2F`
- horizon.vxw: `6259DEA02DB1AE80780C30EADB2F07FF07F7C777B0BCD56B5F1E24B31F5463C6`

## Practical limits

- The 64 MiB packing limit bounds each temporary replication chunk. It does not
  cap the complete LocalChannel receive queue. The measured horizon queue is
  3.00 GiB for one full sync. A stalled consumer, repeated full syncs, multiple
  clients, or indefinite paused edits can retain more. Monitor PendingBytes and
  PeakPendingBytes; add backpressure/resync before supporting slow remote clients.
- The current 64 GB workstation is the validated environment for horizon. Its
  21.50 GiB sampled process footprint excludes rendering that complete world.
  A 16 GB machine is outside the demonstrated envelope for that save.
- Commands sent by tools through CaelixClient obey the pause rule. Server-side
  authoring/import APIs remain immediate writes. Typed query handlers must remain
  read-only. Save/load is an explicit server operation, not a queued edit command.
- Reliable ordered local delivery makes remove/add replacement sufficient here.
  Remote transport, reordered or stale packets, schema negotiation, query timeout,
  hostile input, and dedicated builds remain separate work. This validation does
  not certify public multiplayer readiness.
- Real-save tests use valid trusted files. Transactional recovery from a corrupt
  or interrupted load and a long full-scene GPU soak are not covered.

## Repeating the checks

Use the isolated project at
`F:\RP_Games\Caelix-family\Validation\Titania-server-client`. It is a shared local
clone of Titania at `81225b9`, with its package manifest pointing at isolated
Caelix, Core, and Physics clones in `Validation`. The original Titania checkout
stays on its rayquery branch with its existing changes.

Do not change package snapshots while a test run is compiling or running. The
validated code is:

| Repository | Commit |
|---|---|
| Caelix | `2a67c9325a3e7b099a4a47b3a6643df07e738962` |
| Caelix-Core | `e5ead505253cfb62271ce45a2843146bffa63e85` |
| Caelix-physics | `c10c4e210bfda8c0a176ed03142512bc341f01ce` |
| Titania validation project | `81225b9490b46a5db0f4e216c60428376414b17e` |

Later documentation-only commits do not change the tested code. Both modified
packages must be used together: Core adds the selective channel receive API and
sector attachment identity used by Caelix.

With Unity CLI 1.0.0-beta.6 on PATH, run from PowerShell:

```powershell
$validationArgs = @{
    ProjectPath = 'F:\RP_Games\Caelix-family\Validation\Titania-server-client'
    EditorPath = 'F:\Unity\6000.5.6f1\Editor\Unity.exe'
    OutputDirectory = 'F:\RP_Games\Caelix-family\Validation\runs'
}
& 'F:\RP_Games\Caelix-family\Caelix\Documentation~\Invoke-RndValidation.ps1' @validationArgs -Suite All
& 'F:\RP_Games\Caelix-family\Caelix\Documentation~\Invoke-RndValidation.ps1' @validationArgs -Suite Scale -SavePath 'F:\RP_Games\Titania\saves\horizon.vxw'
```

The script writes timestamped NUnit XML, Editor logs, and stage memory JSON.
`-Suite Lifecycle` runs the focused cases. `-Suite Scale -SyntheticSectors 64`
creates 64 sectors with all 4,096 brick slots allocated and one voxel per brick.
The default synthetic workload is only two sectors so routine tests stay small.
The Editor needs access to the normal Unity licensing service. In Codex's sandbox,
the Unity process required escalation; no interactive Editor window was needed.

Final local artifacts:

- `Validation/final/20260906-033854-all.xml`: 269 passed.
- `Validation/final/20260906-033956-scale.xml`: demo passed.
- `Validation/horizon-scale.xml`: horizon passed.

## Checkpoints and reverting

| Repository | Commit | Purpose |
|---|---|---|
| Core | `e5ead50` | Selective receive, queue byte counters, sector attachment identity |
| Caelix | `6d755e2` | Paused query handling, replacement replication, renderer teardown |
| Caelix | `f39d02d` | Renderer reactivation, authored rebinding, boundary invalidation, Host and scale tests |
| Caelix | `2a67c93` | Windows memory counters and disposal measurement |

Revert dependent Caelix commits before reverting the Core API checkpoint. The
pre-hardening package heads were Caelix `1cdcb51` and Core `9756993`. Physics and
the original Titania checkout were not modified by this implementation.
