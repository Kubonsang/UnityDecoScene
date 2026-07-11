# Design QA — Preference Studio Korean Localization

final result: passed

## Evidence

- Source visual: `design-references/selected-review-studio.png`
- Slider reference: `design-references/slider-reference.png`
- Korean implementation capture: `public/qa/implementation-korean.png`
- Side-by-side comparison: `public/qa-comparison.html`
- Viewport: 1440 × 1024
- Captured state: Korean default locale, Floor selected, rating 1, issue feedback selected

The full product surface fits within the target viewport without horizontal overflow. The Korean header, category labels, assessment controls, and AI proposal remain legible in one 1440 × 1024 frame, so a separate focused crop was not required.

## Findings and fixes

| Priority | Finding | Resolution |
| --- | --- | --- |
| P1 | The prototype had no Korean locale even though the primary user workflow is Korean. | Added complete Korean UI copy, made Korean the default, and retained English as a toolbar option. |
| P2 | Korean labels are longer than the original English labels and could wrap or overflow in compact controls. | Added a Korean-capable font stack, `word-break: keep-all`, and targeted no-wrap rules for toolbar labels and category badges. |
| P2 | Translated display strings could accidentally become application state keys. | Kept locale-neutral category, issue, direction, and importance IDs so feedback state remains stable across language changes. |

No unresolved P0, P1, or P2 findings remain.

## Interaction checks

- Locale switches from Korean to English and back to Korean without losing the interface structure.
- Category selection updates the Korean critique, issue choices, and AI proposal.
- Regenerate shows a generating state and finishes with `미리보기 재생성 완료 · 기술 오류 0`.
- The default page reloads in Korean.
- Document width equals the 1440 px viewport; no horizontal overflow is present.
- Browser console errors and warnings: none.
- Production build: passed with Vite 6.4.2.

## Comparison history

1. Compared the selected Option 2 visual with the Korean implementation at 1440 × 1024.
2. Confirmed the Option 1 slider band and the original review-first hierarchy remain unchanged.
3. Verified the localized toolbar, navigation, rating controls, and AI proposal stay within their panels.
4. Exercised the language switch, category change, and regenerate flow before capturing the final Korean state.

