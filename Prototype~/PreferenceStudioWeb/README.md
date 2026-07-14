# Concept Room Preference Studio Prototype

This interactive prototype demonstrates how a non-technical user can guide a room decorator with high-level art-direction sliders, review an A/B room result, and turn simple ratings into transparent AI weight changes.

It intentionally simulates the workflow rather than implementing the production placement or learning engine.

## Localization

- Korean is the default interface language.
- English remains available from the **언어 / Language** selector in the top toolbar.
- Feedback and slider state use locale-neutral IDs, so changing language does not change the underlying preference data.

## Included flow

- Seven reusable art-direction sliders with importance levels
- Review categories for structure, floor, entrances, wall dressing, props, and lighting
- Before/regenerated room comparison using real project captures
- 1–5 rating and issue-chip feedback
- AI interpretation, hard constraints, weight deltas, confidence, and scope
- Simulated regeneration and technical-safety status

## Local preview

```powershell
npm install
npm run dev
```

The production Unity package remains outside this prototype folder. Scene Apply is deliberately unavailable here.

