# Concept Room Decorator

## Install

Install the package from a local folder or Git URL through Unity Package Manager. The minimum supported Editor version is Unity 6.0.

## Prepare a room

`ConceptRoom` requires an authoring `BoxCollider`. The box is an authoring boundary, not generated room geometry. In **Room Setup**, scan its inward wall faces and ceiling plus one or more flat floor colliders. BoxCollider floors and coplanar MeshCollider floors are supported; curved, sloped, and non-planar meshes remain visible as unsupported evidence and cannot receive props.

Add `RoomObservationPoint` children for entrance or art-direction views. Add `KeepClearZone` children for doors, combat areas, interaction spots, or deliberate negative space. The package performs no pathfinding.

## Prepare a catalog

Create a `DecorCatalog`, select a prefab folder in the Editor window, and scan it. The scanner creates one `DecorAssetDescriptor` asset per prefab. Collider children become compound local OBB proxies; if no collider exists, Renderer local bounds become the proxies. Unchanged dependency hashes skip geometry reanalysis.

Review every descriptor before relying on it:

- Set one consistent `styleSet` for assets that belong together.
- Assign Hero, Support, StoryEvidence, Clutter, LightingCue, or DecalCue roles.
- Assign Floor, Wall, or Ceiling placement.
- Add motifs used by concept required/forbidden checks.
- Check compound OBB proxies, pivot offset, semantic forward/up axes, and bottom/back contact frames.
- Choose FloorSupported, WallBacked, WallMounted, CeilingMounted, or FreeStanding and review its gap, penetration, and support contract.
- Approve geometry only after this check. Source dependency changes automatically invalidate the approval.

## 룸 구성과 미리보기

`RoomCompositionPlan`은 방, 콘셉트 브리프, 카탈로그, 시드, 밀도와 의미 요소를 참조한다. 관계는 앵커 요소 주변에서 해석하며, 최종 Transform은 외부 AI가 아니라 Unity의 결정론적 배치 엔진이 선택한다.

미리보기 오브젝트에는 `DontSaveInEditor`가 적용되며 미리보기 생성 때마다 다시 만들어진다. 잠근 배치는 부분 재생성 중에도 Transform을 유지한다. 실제 Apply는 한 번의 Undo 작업으로 기록된다.

Scene View 오버레이는 OBB와 접촉 증거를 표시한다. 겹침은 빨강, 접촉 실패는 주황, 정상 접촉은 초록이다. 기술 게이트는 `GEOMETRY_UNREVIEWED`, `SURFACE_UNREVIEWED`, `NEED_GEOMETRY_V2`, `OBB_OVERLAP`, `SURFACE_PENETRATION`, `CONTACT_GAP`, `INSUFFICIENT_SUPPORT`, `CONTACT_DIRECTION`, `UNSUPPORTED_SURFACE` 같은 안정적인 오류 코드를 사용한다.

### 토큰 효율적인 단일 룸 검수

**현재 방 검사 / 계속**은 다음 순서로 동작한다.

1. 매 실행마다 저렴한 결정론적 기술 검사를 먼저 수행한다. 충돌, 관통, 간격, 지지율 같은 기술 오류가 하나라도 있으면 캡처와 사용자 승인을 진행하지 않는다.
2. 입력 hash와 기술 검사 hash가 이전 실행과 같으면 기존 4뷰 캡처를 파일 hash까지 확인해 재사용한다. 같은 증거에 연결된 최신 사용자 승인도 그대로 유효하다.
3. 배치, 방 구조, 에셋, 콘셉트, 검증 규칙 또는 표현 설정이 바뀐 경우에만 상단, 주 관찰 시점, 코너 A, 코너 B의 고정 4뷰를 다시 캡처한다. 이전 승인은 `Stale`이 되며 새 캡처에 대한 검수가 필요하다.
4. 실제 사용자가 4뷰를 확인하고 `승인`, `수정 필요`, `판단 불가` 중 하나를 선택한다. 승인은 입력 hash, 기술 보고서 hash, 캡처 세트 hash에 묶여 저장된다.

최종 Apply 조건은 **기술 오류 0개이며 현재 hash 조합에 대한 명시적 사용자 승인이 존재하는 것**이다. AI가 제출한 분위기·스타일·이야기·구도 점수는 수정 제안을 돕는 참고 정보일 뿐, 승인이나 Apply를 해제하지 않는다.

AI에는 이미지, 전체 Transform, 로컬 경로, 전체 씬 계층을 보내지 않는다. 대신 변경 범위, 영향받은 항목 수, 기술 오류 코드와 다음 동작만 담은 작은 JSON 브리프를 제공한다. 따라서 변경이 없는 반복 검사에서는 캡처 분석 토큰을 다시 소비하지 않는다.

## Calibrate Spatial Contracts

Spatial Calibration runs in an additive unsaved scene and never edits the active authoring scene or source prefab. Choose `WallMounted`, `WallBacked + FloorSupported`, `FloorSupported`, or `SupportedBy`, correct compound OBB handles and contact frames, then position the subject as the intended example.

One example defines the reference pose; tolerances still come from the conservative template. The deterministic report exposes gap, penetration, support, and direction alignment for every simultaneous contact. A wall-backed bookshelf therefore must pass both floor support and wall contact.

`Capture Example` produces front, side, top, and contact-close-up PNGs plus raw/evidence variants under `Library/DungeonDecorator/SpatialCaptures`. Draft contracts remain under `Library/DungeonDecorator/SpatialDrafts` until a human approves the latest capture hash.

The Preference Studio's **에셋 공간 규칙 / Asset Geometry** workspace is the human gate. `승인` is disabled when technical errors exist. `수정 필요` records issue types and a comment; `판단 불가` requests a better camera or overlay instead of forcing failure. AI suggestions are displayed separately and never count as approval.

## MCP safety model

The MCP bridge binds only to `127.0.0.1`, generates an Editor-session nonce, and writes connection details under `Library/DungeonDecorator/session.json`. Agent tools can read project context and manipulate preview state only. They cannot call Apply.

Open **Tools > Concept Room Decorator > MCP Bridge Setup** to copy the Node bridge to a stable project-local path and generate project-scoped configuration.

룸 검수 에이전트는 먼저 `prepare_room_review`를 호출한다. 이 도구는 Unity에서 기술 검사를 실행하고, 필요한 경우에만 고정 4뷰를 생성한 뒤 경로와 이미지가 빠진 compact brief를 반환한다. 다음 동작이 `OPEN_REVIEW`이면 에이전트는 작업을 멈추고 실제 사용자가 웹 검수 화면에서 판정할 때까지 기다린다. `get_room_review_agent_brief`는 같은 작은 브리프를 다시 읽을 때 사용한다.

일반적인 룸 검수에서는 에이전트가 캡처 파일을 직접 읽지 않는다. `capture_preview_views`는 명시적인 시각 진단이 필요할 때만 사용하는 고비용 도구다. 기존 `submit_visual_review` 결과 역시 참고용이며 사용자 승인으로 변환되지 않는다. 최종 Apply는 현재 증거 hash에 연결된 사용자 승인이 있을 때만 Unity에서 사용할 수 있다.

## 토큰 절약형 Unity 테스트

열린 Unity 6 Editor에서는 `testplay-runner v0.11.0`의 Warm-Editor Bridge를 우선 사용한다. 기본 회귀 명령은 `Tools~/TestPlay/run-decorator-tests.ps1`이며, `UnityDecoScene.DungeonDecorator` 필터로 패키지 테스트만 실행한다. 성공 시 Unity 원문 로그 대신 `backend`, `exit_code`, `total`, `passed`, `failed`, `skipped`만 포함한 한 줄 JSON을 반환한다.

`backend`가 `bridge`인지 항상 확인한다. Pristine Gate가 warm 결과와 cold 결과의 동등성을 보장할 수 없으면 TestPlay는 자동으로 shadow/process 경로로 폴백한다. `-All`은 호스트 프로젝트 전체 EditMode 기준선을 의도적으로 확인할 때만 사용하며, 실패 원문과 `.testplay/runs/<run_id>/` 아티팩트는 compact JSON만으로 원인을 알 수 없을 때만 읽는다.

The Node bridge optionally starts `unity-ctx mcp` and merges both read-only tool lists. Set `UNITY_CTX_BIN` when the executable is not on `PATH`. Every preview and validation response includes the manifest hash, geometry-profile hash, and seed. The bridge never exposes Unity Apply or a unity-ctx mutation tool.

Spatial tools available to agents are `inspect_spatial_calibration`, `capture_spatial_calibration`, `get_spatial_contract_draft`, `submit_spatial_contract_proposal`, and `get_deterministic_validation_report`. They are intentionally proposal-only. The separate loopback Spatial Review Bridge is the only web path that can invoke the human-reviewed `unity-ctx spatial apply --write` flow.
