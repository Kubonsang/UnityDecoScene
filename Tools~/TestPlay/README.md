# Token-light Unity tests with TestPlay

The default command runs only the Dungeon Decorator EditMode tests and prints one compact JSON object. It does not print Unity logs on success.

```powershell
& "Packages/com.unitydecoscene.dungeon-decorator/Tools~/TestPlay/run-decorator-tests.ps1" `
  -ProjectPath "C:\path\to\UnityProject"
```

The Unity project must contain `testplay.decorator.windows.json`, and TestPlay v0.10.0 plus the matching `com.testplay.bridge` package must be installed. When the Editor is open and the Pristine Gate passes, the result should contain `"backend":"bridge"`.

Use `-Filter` for one test. Use `-All` only when the whole host project's EditMode baseline is intentionally required:

```powershell
& "Packages/com.unitydecoscene.dungeon-decorator/Tools~/TestPlay/run-decorator-tests.ps1" `
  -ProjectPath "C:\path\to\UnityProject" `
  -Filter "UnityDecoScene.DungeonDecorator.Tests.RoomReviewHashTests.Sha256MatchesKnownVector"

& "Packages/com.unitydecoscene.dungeon-decorator/Tools~/TestPlay/run-decorator-tests.ps1" `
  -ProjectPath "C:\path\to\UnityProject" `
  -All
```

On failure the JSON expands only the first 20 compile errors or failed tests. Read `.testplay/runs/<run_id>/` only when that compact evidence is insufficient.
