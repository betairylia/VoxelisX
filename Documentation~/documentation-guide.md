# Write and maintain Caelix documentation

Status: Maintainer guide. Reviewed on 2026-09-06.

Use this guide for Caelix and Caelix Core. Write documentation alongside the code,
in plain English and ordinary Markdown, so a contributor can read it on GitHub
or in a checkout. A reader should be able to find a task, understand its rules,
and locate the implementation and checks that support it.

## Lessons from Unity's package documentation

These are observations from the linked official pages, followed by our choices
for Caelix. The reviewed versions are documentation examples, not a claim that
those package versions are compatible with the current Caelix checkout.

| Package and reviewed pages | Useful pattern | Apply it here |
|---|---|---|
| [URP, Unity 6.0](https://docs.unity3d.com/6000.0/Documentation/Manual/universal-render-pipeline.html), [Render Objects walkthrough](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-features/how-to-custom-effect-render-objects.html), [property reference](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-features/renderer-feature-render-objects.html) | Task navigation; show the intended result, give ordered steps, explain an unexpected result, and link to a separate property table. | Give each how-to one outcome and an observable success check. Keep exhaustive settings in reference pages. |
| [Entities 1.4 overview](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/index.html), [structural changes](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/concepts-structural-changes.html) | The overview distinguishes concepts, programming, authoring, and debugging. A concept page names the operations affected by its rule and links to a workflow. | Introduce entity, sector, brick, slot, and tick before teaching extensions. Explain which operations a lifecycle rule permits. |
| [Physics 1.4 overview](https://docs.unity3d.com/Packages/com.unity.physics@1.4/manual/index.html), [pipeline](https://docs.unity3d.com/Packages/com.unity.physics@1.4/manual/physics-pipeline.html), [collision queries](https://docs.unity3d.com/Packages/com.unity.physics@1.4/manual/collision-queries.html) | Installation and samples have their own entry points. Pipeline documentation names execution phases and extension boundaries; queries have a focused topic. | Document tick order and valid read/write phases. Link a procedure to its data-lifetime contract. |
| [Collections 2.6 types](https://docs.unity3d.com/Packages/com.unity.collections@2.6/manual/collection-types.html), [allocation](https://docs.unity3d.com/Packages/com.unity.collections@2.6/manual/allocation.html) | Type-selection tables link to API details. Memory ownership and allocation have explicit guidance. | Provide an enumerator selection table; state what is selected, required preparation, coordinates, and how long data remains valid. |

URP's current [repository entry page](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Documentation~/index.md)
points Unity 6 users to the Unity Manual and keeps a separate API entry. Do not
assume that its current package directory contains the complete manual.

The [URP API reference](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/api/UnityEngine.Rendering.Universal.ScriptableRendererFeature.html)
uses a regular type/member structure. Keep our public API descriptions in C# XML
comments: purpose, parameters, result, side effects, lifetime, and restrictions.
Manually written pages explain how the APIs work together.

## Arrange the files

Each package owns documentation for the code it implements. Core owns storage,
enumerators, neighborhood reads, serialization primitives, and tick-hook primitives.
Caelix owns world orchestration, server/client workflows, rendering, and authoring.
Cross-package workflows belong in Caelix and link to Core's contracts.

```text
README.md                         short introduction and documentation link
AGENTS.md                         work rules and task-specific reading links
Documentation~/
  index.md                        task navigation and reading order
  documentation-guide.md          this shared guide, owned by Caelix
  manual/                         runnable how-tos and usage explanations
  reference/                      selection tables, formats, settings
  internals/                      ownership, execution order, implementation contracts
  archive/
    index.md                      historical inventory and successors
    design/                       past proposals and decision reports
    validation/                   dated measurements and evidence
```

Create directories when they have content. Keep one canonical page per subject.
Core links to this guide rather than maintaining a second copy. Use descriptive
lowercase filenames for new pages. Retain historical filenames to help searches.

Use relative Markdown links inside a repository. For another repository use its
GitHub URL: sibling-checkout paths do not resolve correctly on GitHub. Normal
cross-repository documentation links target `main` and become available after the
corresponding documentation is merged. For review evidence, link a specific commit.
Coordinate changes in both repositories when a contract spans them.

Keep a short forwarding page at an old published path when moving an established
document. Update maintained navigation to its new path. Move existing sidecar
files with their document. Keep scripts and their expected input paths together;
do not relocate a validation script just to make the directory look uniform.

## Write a page

Start with the outcome or concept in one paragraph. State prerequisites before
instructions. Use the actual API and Inspector names. Define uncommon terms on
first use; keep source identifiers in backticks. Prefer short paragraphs, numbered
procedures, and tables for choices. Use diagrams for ownership or timing, with a
text explanation so their meaning is also available without rendering the diagram.

Choose a shape that fits the page:

| Page | Contents |
|---|---|
| How-to | Goal; prerequisites; ordered steps; minimal example; expected result; troubleshooting; next task. |
| Concept or internal contract | Purpose; model; ownership and coordinates; execution/lifetime rules; constraints; implementation and validation links. |
| Reference | Selection or field table; exact semantics; preconditions; example; related APIs. |
| Decision | Date and status; problem; chosen approach; alternatives and tradeoffs; consequences; superseding decision if any. |
| Validation report | Code revisions; environment; command; results and artifacts; what was not tested. |

Use a compact status line for implementation-sensitive pages, for example:

```text
Status: Current implementation. Checked against Core <commit> on YYYY-MM-DD.
Validation: Source-reviewed example; not executed in Unity.
```

The commit means the code was inspected at that revision. It does not mean the
example compiled, tests passed, or all described configurations were tried.
Say which of those checks actually ran. Update the revision when rechecking a
behavior; do not refresh dates as a cosmetic edit.

Keep a new how-to small enough to complete in one sitting. A partial example must
say what the caller supplies and where it runs. A complete example must include
imports, setup, and disposal. Never invent an API to make an example look simpler.
Keep snippets close to real tests or samples and link them. For diagrams and code,
prefer syntax GitHub renders without a site-specific extension. Explain a Mermaid
diagram in nearby prose. Avoid build-only include directives in essential examples.

## Make pages useful to agents

- Put a reading path in each index: start, change storage, add automata, change
  replication, and investigate rendering.
- State rules as actions with reasons. Example: use the allocated-brick enumerator
  so allocation semantics and traversal optimizations have one implementation.
- Identify the source of each rule: current implementation, maintainer policy,
  or proposed design. Mark implementation gaps explicitly.
- Name source types and test fixtures; use file links rather than fragile line numbers.
- Write coordinate spaces, index meanings, mutation permissions, and lifetime
  constraints beside the API that requires them.
- Keep `AGENTS.md` short. Link the authoritative pages and record required checks.
  An agent should not need to load every historical report to make a small change.

## Maintain documentation with code

For a PR that changes behavior:

1. Identify the affected contract and how-to before changing the API.
2. Update the owning page and public XML comments in the same PR. Add a decision
   record only when the design choice and its tradeoff deserve lasting history.
3. Update examples, navigation, and migration advice for breaking changes. Update
   both package documents if the change requires coordinated versions.
4. Check relative links and anchors, exact symbol names, and code fences. Compile
   changed runnable examples when the Unity dependencies are available. Run the
   relevant existing tests for behavioral changes; record unrun checks explicitly.
5. Review the rendered Markdown in GitHub's PR view, especially tables and diagrams.

Before a release, compare the installation page with package manifests and the
sample project's resolved versions. Review open limitations, migration steps,
and the oldest implementation-sensitive pages. A documentation-only change needs
link and content checks; it does not imply rerunning large simulation benchmarks.

Archive a proposal when it becomes historical, adding its status and a link to the
current explanation. Preserve its original date, evidence, and body. Do not turn
old profiler results into current performance claims. A validation report remains
evidence for its recorded revisions even when the implementation advances.

## Explain with figures

Include a figure when it makes a spatial relationship, ownership boundary,
execution order, or state transition easier to understand. Storage layouts,
coordinate conversions, tick phases, and message sequences are good candidates.
Start with the question the figure should answer, then include only the detail
needed for that answer.

| Format | Use it for | Maintain it by |
|---|---|---|
| SVG | Spatial layouts, annotated examples, and figures whose layout matters. | Keeping editable SVG under the owning package's `Documentation~/images/`. Use embedded styling, readable text, and no external font or script dependency. |
| Mermaid | Small flowcharts and sequence diagrams that change with code. | Keeping the diagram source in the Markdown page and checking its rendered form. |
| Interactive HTML | Stepping through phases, changing inputs, or inspecting a model where interaction helps explain behavior. | Keeping HTML/JS source with the documentation, an adjacent static SVG overview, and a clear instruction for opening or hosting it. |

For example, the [storage figure](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/images/voxel-storage.svg)
distinguishes coordinate indices from compact IDs; the [tick figure](images/tick-and-dirty.svg)
shows where flags are consumed and cleared; the [server/client figure](images/server-and-client.svg)
shows who owns state and how messages cross the boundary.

Embed static figures with a relative Markdown image link. Give each meaningful
alt text and a caption stating the lesson. Keep an SVG `title` and `desc` so its
meaning remains available to tools and assistive readers. Explain essential rules
in the page text as well, so an agent can follow them without image recognition.
Label illustrative data and simplified models explicitly.

Use text labels as well as color. Check contrast and legibility at the width of
a documentation page, including against light and dark surroundings. Keep arrows
unambiguous and put units and coordinate spaces beside the relevant values.
Render an SVG and inspect it for clipped labels and misleading connections before
finishing the change. Update figures and their captions in the same PR as the
behavior they describe.

An interactive figure must have a useful static overview for repository readers.
Label simulated behavior and its limits, and make controls keyboard-accessible.
Include a reset action and a reproducible starting state. Test each control and
its boundary cases. [GitHub Pages](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages)
can host HTML, CSS, and JavaScript; a link to an HTML source file in the repository
is a source-code link, so provide opening instructions or a deployed figure URL.

## Optional publishing

Markdown in the repository is the starting point. If a searchable site and API
browser become useful, [DocFX](https://dotnet.github.io/docfx/) can combine Markdown
with .NET API documentation and publish static output to GitHub Pages. Unity
assembly references and conditional compilation must be supplied correctly before
claiming API generation works for this project. Keep generated output separate
from authored pages; review source changes in PRs.

## Related pages

- [Caelix documentation](index.md)
- [Historical documents](archive/index.md)
- [Core documentation](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/index.md)
