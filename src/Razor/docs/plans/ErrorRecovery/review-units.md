# Razor parser error-recovery redesign: review units

This doc records the relationship between the 33 incremental **sub-stage** branches that produced the redesign and the 6 consolidated **review-unit** branches used to iterate on review feedback.

The sub-stage branches were created during execution, one per agent invocation. They are preserved on the `origin` (chsienki/roslyn) fork as a complete archaeological record. The review-unit branches are squashed re-bases of the sub-stages into logical groups, intended to make review and rebase-cascade iteration tractable.

If a review unit needs to change, edit the **review-unit branch**, not the historical sub-stage branches. The sub-stage branches are immutable.

## Review-unit branches (live; iterate on these)

Each unit is based on the previous unit (or on the base commit for unit 1). All six are tree-equivalent to the corresponding final sub-stage tip.

| Unit | Branch | Tip | Tree matches | Files | Purpose |
|------|--------|-----|--------------|-------|---------|
| 1 | `review-unit-1` | `1161a6beec4b` | `razor-recovery-stage-1-4` | 59 (+2913/-18) | Foundation: primitives + pilot |
| 2 | `review-unit-2` | `a5fbfd04b508` | `razor-recovery-stage-2-6` | 21 (+2365/-40) | CSharpCodeParser migration |
| 3 | `review-unit-3` | `e8a0f0204d1d` | `razor-recovery-stage-3-4` | 9 (+1609/-34) | HtmlMarkupParser migration |
| 4 | `review-unit-4` | `13b089c05f5b` | `razor-recovery-stage-4-4` | 14 (+1332/-54) | Cross-parser handoff |
| 5 | `review-unit-5` | `1416532ce24e` | `razor-recovery-stage-5-6` | 33 (+3640/-37) | Downstream consumers |
| 6 | `review-unit-6` | `285b0cb43b4c` | `razor-recovery-stage-7` | 2065 (+8525/-6365) | Cleanup, docs, benchmarks, ship |

Base commit (parent of `review-unit-1`): `60512eee424b` -- "Fix main build (#83719)". This is the merge-base of the original `razor-recovery-stage-0` with `main`.

## Sub-stage branches (historical; do not iterate on these)

Preserved on `origin/chsienki-roslyn` for archaeology. Each is based on its predecessor in the table; the final tip (`razor-recovery-stage-7`) carries the tag `razor-recovery-redesign-complete`.

| Branch | Tip | Subject |
|--------|-----|---------|
| `razor-recovery-stage-0` | `b1308b3758fb` | Razor recovery Stage 0.4-0.5: SkippedContentSyntax + audit checklist |
| `razor-recovery-stage-1-1` | `e2b1b2187f07` | Razor recovery Stage 1.1: Synchronize + FollowSet primitives |
| `razor-recovery-stage-1-2` | `18c44ecfc6f4` | Razor recovery Stage 1.2: Required + Optional helpers |
| `razor-recovery-stage-1-3` | `05b56498ee67` | Razor recovery Stage 1.3: paired _At diagnostic factories |
| `razor-recovery-stage-1-4` | `b5dd57dd6841` | Razor recovery Stage 1.4: pilot ParseRazorComment migration |
| `razor-recovery-stage-2-1` | `b0f952c4e4d4` | Razor recovery Stage 2.1: ParseExplicitExpressionBody migration |
| `razor-recovery-stage-2-2` | `5601b777f181` | Razor recovery Stage 2.2: ParseStatementBody migration |
| `razor-recovery-stage-2-3` | `ab1ea37e9632` | Razor recovery Stage 2.3: ParseStandardStatement migration |
| `razor-recovery-stage-2-4` | `f095b9f42dc4` | Razor recovery Stage 2.4: TryParseCondition migration |
| `razor-recovery-stage-2-5` | `5a16b2b8bda2` | Razor recovery Stage 2.5: directive parsers migration |
| `razor-recovery-stage-2-6` | `840083096978` | Razor recovery Stage 2.6: ParseMethodCallOrArrayIndex migration |
| `razor-recovery-stage-3-1` | `5e209bca431f` | Razor recovery Stage 3.1: ParseStartTag/ParseEndTag tag-name and close-angle migration |
| `razor-recovery-stage-3-2` | `59ff6d911b99` | Razor recovery Stage 3.2: ParseRemainingAttribute empty C#-bound attribute value migration |
| `razor-recovery-stage-3-3` | `825d4b50ddf6` | Razor recovery Stage 3.3: TryRecoverStartTag / CompleteEndTag precise diagnostics |
| `razor-recovery-stage-3-4` | `5b254a267f66` | Razor recovery Stage 3.4: ParseMiscAttribute migration |
| `razor-recovery-stage-4-1` | `ab838905f65c` | Razor recovery Stage 4.1: cross-parser handoff signature/protocol setup |
| `razor-recovery-stage-4-2` | `c8533bd7e0b0` | Razor recovery Stage 4.2: thread outer follow sets through enhanced-mode recovery |
| `razor-recovery-stage-4-3` | `d2a3a4d2bb4d` | Razor recovery Stage 4.3: implicit-expression markup boundary validation |
| `razor-recovery-stage-4-4` | `1321f9323736` | Razor recovery Stage 4.4: tokenizer state hooks across cross-language sync |
| `razor-recovery-stage-5-0-0` | `4ef35222db92` | Stage 5.0.0: codegen-site spike for empty @onclick="" bug |
| `razor-recovery-stage-5-0` | `a01fce57be70` | Razor recovery Stage 5.0: IR missing-value marker + source-gen UseEnhancedRecovery |
| `razor-recovery-stage-5-1` | `936681d68898` | Razor recovery Stage 5.1: codegen safe placeholders (#10383 visible fix) |
| `razor-recovery-stage-5-2` | `72381adb8b0d` | Stage 5.2: Audit tag-helper rewriters for enhanced-recovery shapes |
| `razor-recovery-stage-5-3` | `0c579f3eb234` | Razor recovery Stage 5.3: source-mapping precision audit |
| `razor-recovery-stage-5-4` | `5ac1f04c1710` | Razor recovery Stage 5.4: FindToken skips zero-width missing tokens |
| `razor-recovery-stage-5-5` | `a17749a13704` | Razor recovery Stage 5.5: formatter audit + regression guards |
| `razor-recovery-stage-5-6-0` | `6fe57c575a31` | Razor recovery Stage 5.6.0: LSP anchor-class spike |
| `razor-recovery-stage-5-6` | `f0ea4b5cc5c1` | Razor recovery Stage 5.6: LSP classification / completion / hover |
| `razor-recovery-stage-6-1` | `4d22c7f14a73` | Razor recovery Stage 6.1: flip UseEnhancedRecovery default to true |
| `razor-recovery-stage-6-2` | `7ffaf2044909` | Razor recovery Stage 6.2: delete legacy UseEnhancedRecovery branches |
| `razor-recovery-stage-6-3` | `44b7ed03714f` | Razor recovery Stage 6.3: documentation update |
| `razor-recovery-stage-6-4` | `15da146f66a9` | Razor recovery Stage 6.4: performance baseline benchmark |
| `razor-recovery-stage-7` | `d6169a046c2c` | Razor recovery Stage 7: persist + hand off |

Tag: `razor-recovery-redesign-complete` -> `15da146f66a9` (head of `razor-recovery-stage-6-4`, the "redesign complete" boundary; Stage 7 housekeeping followed).

## Mapping (sub-stage -> review-unit)

| Sub-stages | Review unit |
|------------|-------------|
| `razor-recovery-stage-0`, `razor-recovery-stage-1-{1,2,3,4}` | `review-unit-1` |
| `razor-recovery-stage-2-{1,2,3,4,5,6}` | `review-unit-2` |
| `razor-recovery-stage-3-{1,2,3,4}` | `review-unit-3` |
| `razor-recovery-stage-4-{1,2,3,4}` | `review-unit-4` |
| `razor-recovery-stage-5-0-0`, `razor-recovery-stage-5-{0,1,2,3,4,5,6-0,6}` | `review-unit-5` |
| `razor-recovery-stage-6-{1,2,3,4}`, `razor-recovery-stage-7` | `review-unit-6` |

## How the squash branches were built

Each unit's tree is exactly the tree of the corresponding final sub-stage. The history is rewritten:

```powershell
# Unit 1: branched from the original base, squash-merged stage-1-4
git checkout -b review-unit-1 60512eee424b
git merge --squash razor-recovery-stage-1-4
git commit -m "..."

# Units 2-6: branched from the target sub-stage tip,
# then soft-reset to the previous unit's head, then committed.
# This avoids spurious add/add conflicts that --squash creates
# when the merge-base is older than the unit base.
git checkout -B review-unit-2 razor-recovery-stage-2-6
git reset --soft review-unit-1
git commit -m "..."
# ... and so on for units 3-6
```

Tree-equivalence was verified after each build by comparing
`git rev-parse <unit>^{tree}` against `git rev-parse <stage-tip>^{tree}`.

## How to iterate on a review unit

Edit `review-unit-N` directly. When you're done, the cascade is:

```powershell
git checkout review-unit-N         # apply your fixes here
# ... edit, commit fixup commits, or amend ...
git checkout review-unit-(N+1)
git rebase --update-refs review-unit-N
# ... resolve conflicts, regenerate baselines if needed ...
git checkout review-unit-(N+2)
git rebase --update-refs review-unit-(N+1)
# ... etc through review-unit-6
```

Setting `git config rebase.updateRefs true` globally is recommended -- it makes the rebase auto-move dependent branch refs in the same operation when they're in the rebased range.

If baselines diverge, regenerate per the `parser-recovery.md` regeneration procedure (or the per-test-suite update mechanism documented in `legacyTest/TestFiles/`).

The sub-stage branches will become stale after any iteration. That is expected; the sub-stage branches are an immutable historical record, not a maintained stack.

## Where to look for context

| Question | Where to look |
|----------|---------------|
| Why does the parser look like this? | `parser-recovery.md` (live contract) |
| Why is recovery structured the way it is? | `razor-parser-analysis.md` (pre-redesign architectural deep-dive) |
| What was the original execution plan? | `razor-recovery-redesign-completed-plan.md` |
| What did each sub-stage actually do? | `razor-recovery-redesign-completed-plan-state.md` |
| Which sub-stages are in which review unit? | This file |
