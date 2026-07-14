# Design QA — Calibration Review Decision Lists

- Source visual truth: [selected-review-studio.png](Prototype~/PreferenceStudioWeb/design-references/selected-review-studio.png)
- Implementation screenshot: [implementation-final.png](Prototype~/PreferenceStudioWeb/public/qa/implementation-final.png)
- Full-view comparison: generated locally during QA and intentionally not committed
- Viewport: 1440 × 1024, dark desktop review workflow
- State: 14 persisted approvals, 1 `RevisionRequested` item (`torch_mounted`), 7 undecided `AwaitingHumanReview` items

## Findings

- No actionable P0, P1, or P2 differences remain.
- The production page preserves the selected direction's three-column top decision overview, left review inbox, selected-item four-view evidence workspace, and explicit decision controls.
- The source mock shows 7 approval-ready items and 0 undecided items. The implementation intentionally shows 0 approval-ready and 7 undecided because the current persisted workflow has no explicit approval-ready decisions. This prevents a four-view check from being misrepresented as approval.

## Required fidelity surfaces

- Fonts and typography: System UI typography, weight hierarchy, Korean labels, and truncation remain consistent with the selected dark desktop direction. All state labels and asset names are legible at the target viewport.
- Spacing and layout rhythm: The compact 203px overview displays all seven rows, the 300px inbox remains distinct from the evidence workspace, and the action area no longer overlays evidence. The final save row needs only the page's small natural scroll rather than hiding content.
- Colors and visual tokens: Charcoal/navy surfaces and restrained green, red, gray, amber, and blue semantic accents match the visual target. Approval, revision, undecided, current selection, and technical status remain distinguishable without relying on copy alone.
- Image quality and asset fidelity: The implementation uses the existing Unity capture images directly for front, side, top, and contact views. No placeholders or approximate image assets were introduced.
- Copy and content: `승인 예정`, `수정 필요`, `미판정`, `4뷰 확인 완료`, and `체크만으로 승인되지 않습니다` are explicit and consistent across overview, inbox, detail banner, and actions.

## Interaction verification

- Selecting `torch_mounted` from the top overview opens its red `수정 필요` detail state.
- Selecting `bookcase_double_decoratedB` from the left inbox restores the undecided detail state.
- `승인 예정` stays disabled until `4뷰 확인 완료` is checked.
- An explicit approval-ready decision moves one item from `미판정 7` to `승인 예정 1`, while final save remains disabled because six items are still undecided.
- Cancelling the local approval-ready decision and unchecking the four-view confirmation restores the original state without writing a contract.
- Browser console warnings/errors: none.

## Comparison history

1. Initial combined build: the sticky action bar overlaid the evidence area after the new top list increased page height. Classified P1.
2. Fix: changed the action bar to normal document flow, compacted overview rows and inbox rows, and changed evidence images to square containers.
3. Post-fix evidence: all seven overview rows are visible, the four-view evidence is unobstructed, and decision controls follow the evidence without overlap.

## Follow-up polish

- P3: The final-save row sits just below the 1024px fold when all seven top-list rows are visible. This is an acceptable tradeoff for showing the complete status overview and keeping evidence unobstructed.

final result: passed
