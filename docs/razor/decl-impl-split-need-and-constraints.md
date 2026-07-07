# The decl/impl markup split — why it's needed and the hard constraints

> **Audience.** A future agent who wants to try a *new approach* to the problem this PR
> solves. This document deliberately describes the **need** and the **constraints**, not
> the current implementation (`ClassBodySplitter`). If a fresh design satisfies every
> constraint in the "Hard constraints" section, it is a valid replacement regardless of how
> it works internally.
>
> **Scope.** This is one PR ("PR #0") in the larger *Sonic* stack for the Razor source
> generator. It is the **first** PR because the perf work in the later PRs depends on it
> being correct. See "Where this sits in the stack" at the end.

---

## TL;DR

Sonic splits each Razor **component**'s generated C# into two `partial class` documents:

- a **decl** document — the component's *public API surface* (class header, type params,
  `[Parameter]`/`[Inject]`/etc. members, sibling methods, route/layout attributes), and
- an **impl** document — the *render logic* (the `BuildRenderTree` body and anything that
  needs resolved tag helpers).

The decl document exists to make cross-project **tag-helper discovery incremental**: it is
produced *early* (before tag-helper discovery) and is engineered to be **byte-stable**
against edits that don't change the API surface. That incremental-ness is the entire perf
win of the later Sonic PRs.

The problem: user `@code { … }` blocks can contain **markup** (e.g. a
`RenderFragment` property whose body is `<div>@Foo</div>`, or a private helper method that
returns a render fragment). Markup can only be lowered **after** tag-helper resolution — but
the decl document is produced **before** it. So the generated decl text must **not** contain
those markup bodies. Yet the **signature** of a surface member (e.g.
`[Parameter] public RenderFragment Header { get; set; }`) **must** remain in decl, or other
pages can't discover it.

**This PR's job:** for every construct a user can write in `@code`, decide which part of it
belongs in the **decl** half (the API-surface/signature) and which part belongs in the
**impl** half (the markup-bearing body), and emit both halves so they **recombine losslessly**
as a single `partial class` at C# compile time.

---

## Background: the two documents and why they exist

Historically the Razor SG emitted **one** generated C# file per `.razor` component. Sonic
splits that into decl + impl. Both are emitted as `partial` and rejoin at C# compile time
into the one component type the user authored.

- **decl** = "what other code needs to know about this component."
  It is the class declaration with its base type / interfaces / generic type parameters /
  user class-level attributes, plus all of the members that form the component's API
  (properties, fields, `[Parameter]`/`[CascadingParameter]`/`[Inject]` members, and sibling
  methods). It **omits** the render-method body and all compiler-synthesized plumbing. Because
  it depends only on user *source* — not on tag-helper resolution — it **can be produced
  earlier in the pipeline** than the final C# lowering.

- **impl** = "how this component renders."
  It is produced by the normal, final C# lowering phase, **after** tag-helper resolution, so
  its markup lowering sees resolved components/elements. It carries the `BuildRenderTree`
  body and any render plumbing.

(The decl phase's own summary comment in
`DefaultRazorDeclCSharpLoweringPhase.cs` is the canonical description of what decl carries and
why it "depends only on user source — not on tag helper resolution — and can therefore run
earlier in the pipeline than the final C# lowering phase.")

---

## The crux: why the decl half must run *before* tag-helper discovery (and can't just move later)

This is the question a future agent will ask first: *"If markup in `@code` is a problem only
because decl runs before resolution — why not just run the decl split later, after resolution,
where markup is fine?"*

**Because running early is the entire point.** The decl document is not a convenience; it is a
**performance mechanism** for incremental compilation in the IDE. The mechanism only works if
decl is produced *before*, and is *independent of*, tag-helper discovery:

1. Razor components are discovered as tag helpers **from the compilation**. To let *other*
   pages see this component (for completion, for resolving `<ThisComponent … />`), its
   generated type must be in the compilation that tag-helper discovery scans. Discovery is
   **symbol-based**: it reads the component type's *signatures* (its `[Parameter]` properties,
   class header, type params) — it never reads method bodies.

2. Tag-helper discovery is **expensive**: it walks the compilation's assembly/types to build
   the descriptor set. You do not want to re-run it on every keystroke.

3. The Sonic SG feeds the **decl** document (not the full generated file) into that discovery
   step, via `RegisterPreCompilationSourceOutput`. The pre-compilation cache key is
   **reference-based on the `SourceText`** (`ReferenceEquals`). So: **if the decl `SourceText`
   is byte-for-byte identical to the previous edit's, the cache key hits, discovery does not
   re-run, and every downstream incremental-generator step stays cached.**

4. Therefore the decl text must depend **only on the API surface** and must **not change**
   when the user edits something that isn't the API surface — e.g. typing inside a render
   block, or inside a markup body in `@code`. (This is also why PR #1 suppresses the
   `#pragma checksum` on the decl half: otherwise a markup-only edit changes the checksum
   hash, makes the decl `SourceText` byte-different, misses the cache key, and "forces a full
   re-walk of `compilation.Assembly` for tag helper discovery on every keystroke.")

**Consequences that become hard constraints:**

- If decl ran *after* resolution, decl would depend on resolved tag helpers, so **every markup
  edit would invalidate decl**, blow the cache key, and re-trigger discovery per keystroke —
  destroying the perf win. So **decl must run before discovery.** Moving it later is not an
  option; it defeats the reason decl exists.
- Running before discovery means decl **cannot see resolved tag helpers.**
- Markup (elements/components) can only be lowered **with** resolved tag helpers.
- Therefore **markup-bearing bodies cannot appear in the decl text** — they must be routed to
  impl — **while the API-surface signature of the owning member must stay in decl.**

That last line is the need this PR fills.

---

## Why a split is even *feasible*: surface vs. markup are structurally disjoint

A split is only possible because "what contributes to the tag-helper descriptor" and "what can
contain markup" never overlap:

- **What contributes to the descriptor surface** (what discovery reads): the class header
  (name, namespace, base type, interfaces, generic type parameters, constraints), user
  class-level attributes (route/layout), and **member *signatures*** — specifically `public`,
  non-static properties carrying a Blazor parameter attribute (`[Parameter]`, and the
  editor-required/cascading/supply-from variants) with a public setter. Discovery reads
  **signatures only**, **never bodies**.

- **What can contain markup**: only **bodies** — a property's expression body / initializer /
  accessor body, or a method body. Markup never appears in a signature.

So the descriptor surface is, by construction, **markup-free**, and the markup always lives in
bodies. That is what makes it possible to keep the surface in decl and move the bodies to impl.
The one member that must appear in *both* halves is a **surface property whose body contains
markup** (e.g. a `[Parameter] public RenderFragment Foo => <div/>;`): its *signature* is part
of the descriptor surface (must be in decl) but its *body* needs resolution (must be in impl).

---

## The need, stated crisply

> For each Razor component, produce a **decl** C# document that (a) contains the component's
> complete tag-helper descriptor surface, (b) contains **no** construct that requires tag-helper
> resolution (i.e. no markup), and (c) is **byte-stable** against edits that don't change the
> descriptor surface — **and** an **impl** C# document that contains everything else (all
> markup-bearing bodies, lowered with resolved tag helpers), such that decl + impl **recombine
> losslessly** into the single component type the user wrote.
>
> The hard part is the set of `@code` constructs that mix the two: a member whose **signature**
> is descriptor surface but whose **body** contains markup. The PR must place the signature in
> decl and the body in impl without changing the program's meaning or its diagnostics.

---

## Hard constraints

Any approach — current or new — must satisfy **all** of these. Each is stated with the reason
it exists so a new design can reason about it rather than cargo-cult it.

### C1. Descriptor completeness (decl must be a faithful API surface)
The decl half must expose the component's **complete and correct** descriptor surface: class
header, generic type parameters (and constraints), route/layout attributes, and **every**
`[Parameter]`/cascading/inject member with its **real type and accessibility**. Cross-page
tag-helper discovery reads *only* the decl-derived type. A missing or mis-typed surface member
silently corrupts other pages' completion and codegen.
*Corollary:* a markup-bearing surface property may **not** simply be moved wholesale to impl —
its signature must remain visible in decl.

### C2. Decl must contain nothing that needs tag-helper resolution
No markup (elements, components, `@:` text, `<Foo>`), because decl is produced before
resolution. Unresolved markup either fails to lower or lowers incorrectly. Equivalent: the decl
text must be derivable **without** the resolved tag-helper set.

### C3. Decl must be byte-stable against non-surface edits
Editing markup inside a `@code` body, or inside the render block, must **not** change the decl
document's bytes. The SG's pre-compilation cache key is `ReferenceEquals` on the decl
`SourceText`; any byte change is a cache miss that re-runs tag-helper discovery. (This is the
perf reason decl exists — see "The crux".) This also means the split decision itself must be
**stable**: the same source must always yield the same decl bytes.

### C4. Lossless recombination (decl + impl == the user's type)
decl and impl are `partial` halves of the **same** type (same name, namespace, arity,
constraints). At C# compile time they must reconstitute exactly the type the user authored:
- **No member duplicated** across halves (a property can't be fully emitted in both — that's a
  duplicate-member error). The mixed case (C1 corollary) must therefore emit a *partial* form:
  something in decl that stands in for the member, and something in impl that supplies the body,
  with no double definition.
- **No member dropped.**
- **Semantics preserved**: accessibility, `static`, `init`-only vs `set`, `readonly`,
  attributes, default-interface details, nullability annotations, `field` keyword usage, etc.
  A transform that turns `init` into `set`, or drops an attribute, is wrong.

### C5. Diagnostics must be preserved, not masked or relocated
If the user's `@code` doesn't compile, the split must produce the **same diagnostic the C#
compiler would report for the un-split document**, at a sensible location. The split must not:
- hide an error by moving code somewhere it no longer errors,
- introduce a *new* error the un-split code didn't have, or
- attribute an error to generated scaffolding the user can't see.
Practically this means: constructs the split doesn't understand, or that only occur in invalid
code, should be passed through **untransformed** (typically left in decl as-written) so the
compiler speaks for itself. Prefer an **allowlist** of shapes you transform over a denylist of
shapes you refuse.

### C6. decl and impl stay separate end-to-end (no recombining as a shortcut)
The two halves must remain **distinct documents** throughout the pipeline. They participate in
**different phases** of the SG pipeline in the later Sonic PRs (decl feeds discovery early;
impl feeds final compilation late). Collapsing them back into one document — even if it
happens to compile — is **disallowed**, because it removes the very seam the perf work relies
on. *(This is a firm project constraint, not a preference.)*

### C7. The original IR must remain walkable in its pre-split shape
Various consumers walk the original `DocumentIntermediateNode` (e.g. namespace matching,
"extract to code-behind", other IDE features). The split must not mutate the original IR out
from under them; producing decl must leave the original tree observably unchanged. *(This is a
constraint on **how** you produce the halves, not just what they contain. A new approach that
reshapes IR in place must still present the pre-split shape to these consumers — or update
every consumer, which is a much larger blast radius.)*

### C8. Cohost text-identity (IDE local view must match SG-emitted text)
In cohosting, the IDE computes a **local** view of the generated C# (`GetCSharpSourceText`) and
Roslyn compiles the **SG-emitted** text; formatting and code-action edits are computed against
one and applied to the other. Whatever the split emits must be produced **identically** on both
the SG path and the local-view path — same decl bytes, same impl bytes, same mapping — or edits
land at the wrong offsets. (Empirically, a mismatch here produces out-of-range crashes in
cohosting tests.)

### C9. Source mappings / debuggability must survive
Generated C# carries source mappings (and, for impl, `#pragma checksum` for the debugger). The
split must keep user code mapped back to the correct original spans in whichever half it lands,
so breakpoints, diagnostics positions, and go-to-definition stay correct. (Note the asymmetry:
impl keeps its checksum for debugging; decl suppresses it for stability — see C3.)

### C10. Generic components still infer their type arguments
Razor does not resolve implicit generic component type arguments itself; it emits
`ComponentTypeInferenceMethod` helpers for the C# compiler to infer them. Those helpers live on
the render/impl side. The decl half must still declare the class's **generic type parameters**
(they're descriptor surface — C1), while the inference helpers stay in impl. A new approach must
keep that division so generic components both *discover* and *infer* correctly.

### C11. Minimize churn for the common case
The overwhelmingly common `@code` block is **pure C# with no markup**. For those, the split
should be a **no-op** on the generated output (all members stay in decl; impl is unchanged from
the single-file baseline). This matters for (a) review sanity — small diffs — and (b) C3: less
generated text that could destabilize. A design that reshapes markup-free `@code` is doing
unnecessary work and risking cache misses.

### C12. Cost is paid per-component, twice — keep it cheap
The split decision is computed on the SG hot path, and (in the current pipeline) is needed by
**both** lowering phases (decl and impl). Whatever analysis decides the routing should be cheap
and ideally computed once and shared. Avoid doing heavy work (e.g. a full C# parse) for the
common markup-free case (ties back to C11).

---

## Non-goals / explicitly out of scope

- **Non-components.** Legacy MVC/`.cshtml` Razor and any document without the component
  primary structure are **not** split; the phase is a no-op and the SG falls through to
  single-file behavior. A new approach only needs to handle components.
- **Suppressed-render-body documents.** If the primary render method body is suppressed, there
  is nothing to split.
- **Making invalid code compile.** The split's job is not to fix or improve user errors (C5) —
  only to preserve them.
- **Changing the descriptor model.** What counts as descriptor surface is defined by Blazor's
  component model; the split consumes that definition, it doesn't redefine it.

---

## A checklist to validate any new approach

A fresh design is acceptable iff, for **every** `@code` shape below, it produces a decl that
satisfies C1–C3 and an impl such that C4–C10 hold:

1. Pure-C# `@code` (no markup) → **no output change** (C11): all members in decl, impl == baseline.
2. Private helper method returning markup → body in impl; nothing about it in decl.
3. `[Parameter] public RenderFragment Foo { get; set; }` with **no** markup → wholly in decl.
4. `[Parameter] public RenderFragment Foo => <div>@x</div>;` (expression body, markup) →
   **signature/stub in decl, markup body in impl**, recombining losslessly (C4).
5. `[Parameter] public RenderFragment Foo { get; set; } = <div/>;` (markup initializer) →
   surface in decl, initializer body in impl; preserve `init` vs `set` (C4).
6. Markup in **multiple accessors** of one property → each markup site routed to impl; the
   property signature stays in decl.
7. A `field`-keyword property with markup in an accessor **and** the initializer → all markup
   sites to impl; signature in decl.
8. Aliased `RenderFragment` (`@using RF = …; [Parameter] public RF Foo => <div/>;`) → treated
   the same as the un-aliased shape (the alias is only visible in source, not via resolution).
9. **Invalid** shapes that happen to contain markup (e.g. a `[Parameter]` on a field, a markup
   body on a non-`RenderFragment` type) → **passed through untransformed**, same compiler
   diagnostic as un-split (C5).
10. Editing only the markup in any of the above → **decl bytes unchanged** (C3).

If all ten hold, and C6–C8/C12 are honored structurally, the approach is viable.

---

## Where this sits in the stack (context, not constraints)

This PR is the **base** of a three-PR stack. The two PRs above it are what make the perf win
real; this PR is the correctness prerequisite for them. **Branch names and commit lists are
recorded below so a future agent can find the actual code** — they were exploratory/feature
branches, so treat the SHAs as starting points, not permanent references.

### PR #0 — this PR: the decl/impl markup split
- **Branch:** `sonic/N_decl_markup_helpers`
- **Base:** `e2805dbba00` ("Add render-tree-builder-call baseline for component codegen tests
  (#84162)", on `features/sonic`).
- **Commits:**
  - `deba1097c07` Split component declarations from markup-bearing bodies via ClassBodySplitter
  - `cffb690e959` Add test coverage for the decl/impl markup splitter
- **What it does:** decides, for every `@code` construct, which part is descriptor surface
  (→ decl) vs markup body (→ impl), and emits both `partial` halves so they recombine losslessly.
  Makes the decl half free of tag-helper references so it is safe to relocate earlier. Lands
  **first** because the perf PRs are unsafe without it. (The current implementation is
  `ClassBodySplitter.cs` — but this document exists precisely so a new approach can replace it.)

### PR #1 — pipeline / perf
- **Branch:** `sonic/4_pipeline_perf`
- **Base:** `39d8f62bcf70` (an earlier `features/sonic` point — note this is a *different* base
  than PR #0).
- **Commits (bottom → top):**
  - `d4f20fc9899` Move ComponentWhitespacePass to optimization phase
  - `f4913e2c539` Reorder Razor engine phases so decl C# lowering runs before tag-helper discovery
  - `540658868d1` Restructure SG pipeline to emit decl via RegisterPreCompilationSourceOutput
  - `8133aeb93c8` Suppress decl #pragma checksum
  - `c5ce86d928b` Replay IR from cached syntax tree on material tag-helper change
  - `4bc65c0633b` Document UTF-8 BOM preservation rule in Razor instructions
- **What it does:** the actual perf change. Moves decl C# lowering to run **before** tag-helper
  discovery, switches the SG to `RegisterPreCompilationSourceOutput`, suppresses the decl
  `#pragma checksum` (C3), and replays IR from the cached syntax tree when only the material
  tag-helper set changes. **This is where the RPS win is realized — and where an un-split decl
  would start silently dropping markup in `@code`.** PR #0 is the thing that makes this move
  correct.

### PR #2 — prototype fixes
- **Branch:** `sonic/5_prototype_fixes`
- **Base:** `39d8f62bcf70` (same as PR #1). **This branch integrates PR #1's perf work *and*
  the prototype fixes** — its tree already contains the phase reorder plus the fixes below. (So
  `sonic/4_pipeline_perf` is the clean standalone perf PR, while `sonic/5_prototype_fixes` is
  the combined branch that was actually used for the RPS measurement build; see below.)
- **Commits (bottom → top):**
  - `ad6bfa50117` Sonic 4/5: Restructure SG pipeline to emit decl via RegisterPreCompilationSourceOutput
  - `5d676ea7361` Sonic 4 test fixups: phase order changes and IR-lowering diagnostic emission
  - `f063dd90639` Sonic 4 test fixups: SG ordering, MVC v1_X, diagnostic position, decl baselines
  - `bebf1877e72` Suppress decl #pragma checksum to fix incremental SG perf
  - `a1a4b1af9c7` Rebuild IR from cached syntax tree on material tag helper change
  - `57ed50d3b62` Code review fixes from sonic-4 review
  - `84711d83659` Skip impl emission of unresolvable @using directives
  - `e9ea9a4b85a` Strip BaseType/Interfaces/constraints from impl class header
  - `8af9ee10119` Update test assertions and inline baselines for impl class-header strip
- **What it does (the fixes on top of PR #1):**
  - **Strips base type / interfaces / constraints from the *impl* class header** — they're
    descriptor surface (C1), so they belong to decl; leaving them on impl too would double them.
  - **Skips impl emission of unresolvable `@using` directives** (a `@using` that only resolves
    against something decl-only would otherwise error in impl).
  - Plus the accompanying test/baseline updates.

### How they were combined for measurement
For the RPS/perf measurement build, the stack was assembled as
`features/sonic` + `sonic/5_prototype_fixes` (PR #1 + PR #2) + PR #0's two splitter commits on
top (branch `sonic/rps-measurement`). Using `5_prototype_fixes` alone (rather than
`4_pipeline_perf` *then* `5_prototype_fixes`) avoids a duplicate-restructure collision, because
`5_prototype_fixes` already contains the reorder.

### The through-line
The reason PR #0's decl text must be resolution-independent and byte-stable only becomes
*visible* in PR #1 — but it's a property PR #0 must already guarantee, which is why the split
decision is designed to use **only** information available pre-resolution (user source: the
member signatures, attributes, and `@using` alias map), never the resolved tag-helper set. A
new approach to PR #0 must preserve that same discipline, because PR #1 and PR #2 depend on it.

---

## Key facts a new design will want verified (with where to look)

- **Decl carries the API surface, omits render body + synthesized plumbing, runs early:**
  `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/DefaultRazorDeclCSharpLoweringPhase.cs` (class summary).
- **The byte-stability / cache-key / re-discovery-per-keystroke reasoning:** the decl
  `#pragma checksum` suppression comment in the same file on the PR #1/#2 branch
  (`sonic/5_prototype_fixes`), and the SG pre-compilation cache key
  (`ReferenceEquals` on `SourceText`) in
  `src/Compilers/Core/Portable/SourceGeneration/CompilationCache.cs`.
- **Discovery is symbol-based and reads signatures only (never bodies):** the component
  tag-helper producer under
  `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Components/`.
- **Tag-helper resolution/lowering replaces IR nodes (identity/child order not preserved):**
  the resolution + component-lowering passes referenced from
  `RazorProjectEngine.cs` phase ordering — relevant if a new approach hopes to correlate
  nodes across resolution.
- **Generic components emit inference helpers rather than self-resolving type args:**
  `.../Components/ComponentGenericTypePass.cs`.
