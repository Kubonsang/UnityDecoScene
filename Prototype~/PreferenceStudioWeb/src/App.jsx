import { useEffect, useMemo, useState } from "react";
import {
  ArrowClockwise,
  Brain,
  CaretDown,
  Check,
  CheckCircle,
  Cube,
  DoorOpen,
  GridFour,
  ImageSquare,
  Lightbulb,
  ListChecks,
  LockKey,
  ShieldCheck,
  SlidersHorizontal,
  Sparkle,
  SquaresFour,
  WarningCircle,
} from "@phosphor-icons/react";

const spatialTranslations = {
  ko: {
    studio: "에셋 공간 규칙", calibration: "공간 캘리브레이션", queue: "검수 대기 에셋", queueHelp: "Unity에서 캡처한 사례입니다. 기술 검사를 통과해도 사용자 승인이 있어야 활성화됩니다.",
    evidence: "캡처 증거", raw: "원본", overlay: "접촉 정보", views: { front: "정면", side: "측면", top: "상단", contact: "접촉 확대" },
    technical: "기술 게이트", human: "사용자 게이트", passed: "오류 0개", failed: "기술 오류", pending: "승인 대기", approved: "승인됨", revision: "수정 필요", unable: "판단 불가",
    measurements: "정확한 측정값", gap: "간격", penetration: "관통", support: "지지율", direction: "방향 일치", allowed: "허용", relation: "관계", frames: "접촉 프레임", contract: "계약 상태", captureHash: "캡처 해시",
    issuePrompt: "문제 영역", issues: ["OBB가 모델과 다름", "접촉면이 부자연스러움", "간격이 어색해 보임", "카메라로 판단하기 어려움"], comment: "의견을 남겨 주세요", approve: "승인", request: "수정 필요", cannotJudge: "판단 불가", approvalBlocked: "기술 오류가 있어 승인을 사용할 수 없습니다.",
    aiTitle: "AI 임시 제안", aiBody: "AI는 back 프레임과 허용 간격을 제안할 수 있지만, 통과·승인·저장은 할 수 없습니다.", recapture: "재촬영 요청", latest: "최신 캡처", reviewer: "로컬 검수자: student-01", saved: "보안 검수 페이지를 열었습니다. 최종 판정은 그 페이지에서 완료하세요.", prototypeSaved: "프로토타입 판정만 변경되었습니다 · 로컬 브리지 연결 안 됨", bridgeOn: "로컬 승인 브리지 읽기 전용 연결", bridgeOff: "프로토타입 모드", liveCapture: "실제 Unity 캡처",
  },
  en: {
    studio: "Asset Geometry", calibration: "Spatial Calibration", queue: "Assets awaiting review", queueHelp: "Examples captured in Unity. Technical pass alone never activates a contract; human approval is required.",
    evidence: "Capture evidence", raw: "Raw", overlay: "Contact evidence", views: { front: "Front", side: "Side", top: "Top", contact: "Contact close-up" },
    technical: "Technical gate", human: "Human gate", passed: "0 errors", failed: "Technical errors", pending: "Awaiting approval", approved: "Approved", revision: "Revision requested", unable: "Unable to judge",
    measurements: "Exact measurements", gap: "Gap", penetration: "Penetration", support: "Support", direction: "Direction", allowed: "Allowed", relation: "Relation", frames: "Contact frames", contract: "Contract state", captureHash: "Capture hash",
    issuePrompt: "Problem area", issues: ["OBB does not match model", "Contact frame looks wrong", "Gap looks unnatural", "Camera is insufficient"], comment: "Add review notes", approve: "Approve", request: "Request revision", cannotJudge: "Unable to judge", approvalBlocked: "Approval is disabled while technical errors exist.",
    aiTitle: "Temporary AI proposal", aiBody: "AI may suggest the back frame and tolerances, but cannot pass, approve, or save a contract.", recapture: "Request recapture", latest: "Latest capture", reviewer: "Local reviewer: student-01", saved: "Opened the secure review page. Complete the final decision there.", prototypeSaved: "Prototype state only · local review bridge disconnected", bridgeOn: "Local approval bridge · read only", bridgeOff: "Prototype mode", liveCapture: "Live Unity capture",
  },
};

const calibrationCases = [
  { id: "banner", name: "Banner_Red", relation: "WallMounted", frames: "back → wall", errors: 0, gap: "0.008 m", penetration: "0.000 m", support: "100%", direction: "0.998", state: "pending", hash: "8f2c…91a7" },
  { id: "bookshelf", name: "Bookshelf_Wide", relation: "FloorSupported + WallBacked", frames: "bottom → floor · back → wall", errors: 0, gap: "0.031 m", penetration: "0.000 m", support: "94%", direction: "0.992", state: "pending", hash: "41bd…d02e" },
  { id: "bottle", name: "PotionBottle_A", relation: "SupportedBy", frames: "bottom → table.top", errors: 1, gap: "0.018 m", penetration: "0.000 m", support: "42%", direction: "0.981", state: "failed", hash: "c931…ee42" },
];

const directionDefaults = [
  { id: "order", value: 25, importance: "high" },
  { id: "density", value: 43, importance: "medium" },
  { id: "variation", value: 28, importance: "high" },
  { id: "damage", value: 37, importance: "medium" },
  { id: "symmetry", value: 32, importance: "low" },
  { id: "drama", value: 61, importance: "high" },
  { id: "story", value: 67, importance: "high" },
];

const categories = [
  { id: "structure", icon: SquaresFour, score: 4, issues: ["purpose", "partitions", "hierarchy", "corners"] },
  { id: "floor", icon: GridFour, score: 1, issues: ["specialTiles", "repetition", "walkway", "damage"] },
  { id: "entrances", icon: DoorOpen, score: 2, issues: ["entranceCount", "arrivalView", "mainDoor", "focalBlock"] },
  { id: "walls", icon: ImageSquare, score: 2, issues: ["cornerGap", "pillarConflict", "height", "rhythm"] },
  { id: "props", icon: Cube, score: 4, issues: ["heroHidden", "relationships", "clutter", "supportZone"] },
  { id: "lighting", icon: Lightbulb, score: 4, issues: ["flat", "overexposed", "heroDark", "colorContrast"] },
];

const translations = {
  ko: {
    localeName: "한국어",
    brand: "콘셉트 룸 데코레이터",
    prototype: "선호도 프로토타입",
    studio: "리뷰 스튜디오",
    interactive: "인터랙티브 프로토타입",
    themeLabel: "테마",
    scopeLabel: "피드백 적용 범위",
    languageLabel: "언어",
    themes: { dungeon: "던전", temple: "사원", sciFi: "SF 연구실", tavern: "여관" },
    scopes: { room: "현재 방만", theme: "던전 테마", project: "프로젝트 선호도" },
    safety: "기술 안전성",
    locked: "잠김",
    artDirection: "아트 디렉션",
    artDirectionHelp: "평가 전에 원하는 결과 방향을 조정하세요",
    reset: "초기화",
    importance: { low: "낮음", medium: "보통", high: "높음", must: "필수" },
    directions: {
      order: ["정돈됨", "혼란스러움"],
      density: ["희박함", "조밀함"],
      variation: ["균일함", "다양함"],
      damage: ["온전함", "파손됨"],
      symmetry: ["대칭", "비대칭"],
      drama: ["기능적", "극적"],
      story: ["절제됨", "이야기 풍부"],
    },
    reviewCategories: "리뷰 항목",
    categoryHelp: "항목을 선택하면 해당 평가와 AI 제안에 집중할 수 있습니다.",
    statuses: { needsWork: "개선 필요", neutral: "보통", good: "좋음" },
    categories: {
      structure: { label: "구조", summary: "방의 외곽이 명확하고 중심 영역에 충분한 여백이 있습니다." },
      floor: { label: "바닥", summary: "특수 바닥 타일의 용도가 불분명해 시각적 소음이 생깁니다." },
      entrances: { label: "출입구", summary: "나란히 놓인 두 출입구가 시선을 경쟁해 방의 진입 방향이 모호합니다." },
      walls: { label: "벽면 장식", summary: "장식 설치 영역을 명확히 하고 모서리와 기둥에서 더 떨어뜨려야 합니다." },
      props: { label: "소품", summary: "중심 소품과 보조 소품이 적절한 여백을 두고 읽기 쉬운 군집을 만듭니다." },
      lighting: { label: "조명", summary: "따뜻한 중심광과 차가운 보조광이 가독성을 유지하며 던전 분위기를 만듭니다." },
    },
    issues: {
      purpose: "방의 용도가 불명확함", partitions: "칸막이가 너무 많음", hierarchy: "중심 위계가 약함", corners: "사용되지 않는 모서리",
      specialTiles: "의미 없는 특수 타일", repetition: "지나치게 반복됨", walkway: "이동 영역을 끊음", damage: "파손 표현이 과함",
      entranceCount: "출입구가 너무 많음", arrivalView: "진입 시점이 약함", mainDoor: "주 출입구가 불명확함", focalBlock: "중심 시야를 가림",
      cornerGap: "모서리에 너무 가까움", pillarConflict: "기둥과 충돌함", height: "설치 높이가 잘못됨", rhythm: "시각적 리듬이 불균일함",
      heroHidden: "중심 소품이 가려짐", relationships: "소품 관계가 불명확함", clutter: "소품이 너무 많음", supportZone: "보조 영역이 비어 있음",
      flat: "조명이 평면적임", overexposed: "중심부가 과다 노출됨", heroDark: "중심 소품이 너무 어두움", colorContrast: "색 대비가 지나치게 강함",
    },
    before: "이전",
    currentPreview: "현재 미리보기",
    regenerated: "재생성",
    updatedFromFeedback: "피드백 반영 완료",
    proposedPreview: "제안 미리보기",
    generatingPreview: "미리보기 생성 중…",
    beforeAlt: "복잡한 바닥 패턴이 있는 현재 던전 방 미리보기",
    afterAlt: "시각적 위계를 정리한 재생성 던전 방 미리보기",
    assessment: "내 평가",
    ratingLabels: ["매우 나쁨", "나쁨", "보통", "좋음", "매우 좋음"],
    whatFeelsWrong: "어떤 점이 어색한가요?",
    selectAll: "복수 선택 가능",
    aiProposal: "AI 변경 제안",
    understood: "이렇게 이해했어요",
    proposals: {
      floor: "단순하고 연속적인 석재 타일을 우선합니다.",
      entrances: "주 출입구를 정확히 하나만 유지합니다.",
      improvePrefix: "가독성을 높입니다:",
      heroLock: "현재 중심 소품의 위치는 잠급니다.",
      applyTo: "적용 범위:",
    },
    hardConstraints: "필수 제약",
    entranceConstraint: "주 출입구 = 1",
    safetyPreserved: "안전 규칙 유지",
    purposeRequired: "용도 명확성 필수",
    weightChanges: "가중치 변화",
    weights: { floorContinuity: "바닥 연속성", specialTileRarity: "특수 타일 희소성", negativeSpace: "여백", storyEvidence: "이야기 흔적" },
    confidence: "해석 신뢰도",
    generating: "생성 중…",
    regenerate: "변경 사항으로 재생성",
    keepCurrent: "현재 결과 유지",
    developer: "개발자 · 가중치 및 신호",
    seed: "시드 240711",
    previewOnly: "미리보기 전용 · 씬 적용 불가",
    notices: {
      ready: "AI 제안 준비됨",
      updated: "해석이 업데이트됨",
      feedback: "피드백이 제안에 반영됨",
      generating: "결정론적 미리보기 생성 중…",
      regenerated: "미리보기 재생성 완료 · 기술 오류 0",
      reset: "아트 디렉션 초기화됨",
      kept: "현재 미리보기를 유지함",
    },
  },
  en: {
    localeName: "English",
    brand: "Concept Room Decorator",
    prototype: "Preference Prototype",
    studio: "Review Studio",
    interactive: "INTERACTIVE PROTOTYPE",
    themeLabel: "Theme adapter",
    scopeLabel: "Feedback scope",
    languageLabel: "Language",
    themes: { dungeon: "Dungeon", temple: "Temple", sciFi: "Sci-Fi Lab", tavern: "Tavern" },
    scopes: { room: "This room only", theme: "Dungeon theme", project: "Project preference" },
    safety: "Technical safety",
    locked: "Locked",
    artDirection: "Art Direction",
    artDirectionHelp: "Guide the result before you critique it",
    reset: "Reset",
    importance: { low: "Low", medium: "Medium", high: "High", must: "Must" },
    directions: {
      order: ["Ordered", "Chaotic"], density: ["Sparse", "Dense"], variation: ["Uniform", "Varied"],
      damage: ["Intact", "Ruined"], symmetry: ["Symmetric", "Asymmetric"], drama: ["Functional", "Dramatic"], story: ["Subtle", "Story-rich"],
    },
    reviewCategories: "Review categories",
    categoryHelp: "Select a category to focus the critique and AI proposal.",
    statuses: { needsWork: "Needs work", neutral: "Neutral", good: "Good" },
    categories: {
      structure: { label: "Structure", summary: "The room shell reads clearly and keeps the focal area open." },
      floor: { label: "Floor", summary: "The floor uses special tiles without a clear function and creates visual noise." },
      entrances: { label: "Entrances", summary: "Two adjacent entrances compete for attention and make the room's approach ambiguous." },
      walls: { label: "Wall Dressing", summary: "Wall dressing needs clearer mounting zones and more distance from corners and pillars." },
      props: { label: "Props", summary: "The hero and supporting props form a readable cluster with useful negative space." },
      lighting: { label: "Lighting", summary: "Warm focal light and cool fill create a strong dungeon mood without losing readability." },
    },
    issues: {
      purpose: "Room purpose unclear", partitions: "Too many partitions", hierarchy: "Weak focal hierarchy", corners: "Unused corners",
      specialTiles: "Meaningless special tiles", repetition: "Too repetitive", walkway: "Breaks walkway", damage: "Too much damage",
      entranceCount: "Too many entrances", arrivalView: "Weak arrival view", mainDoor: "No clear main door", focalBlock: "Blocks focal view",
      cornerGap: "Too close to corner", pillarConflict: "Conflicts with pillar", height: "Wrong height", rhythm: "Uneven visual rhythm",
      heroHidden: "Hero is obscured", relationships: "Relationships unclear", clutter: "Too much clutter", supportZone: "Empty supporting zone",
      flat: "Too flat", overexposed: "Overexposed focal area", heroDark: "Hero too dark", colorContrast: "Color contrast too strong",
    },
    before: "Before",
    currentPreview: "Current preview",
    regenerated: "Regenerated",
    updatedFromFeedback: "Updated from feedback",
    proposedPreview: "Proposed preview",
    generatingPreview: "Generating preview…",
    beforeAlt: "Current dungeon room preview with noisy floor patterns",
    afterAlt: "Regenerated dungeon room preview with clearer visual hierarchy",
    assessment: "Your assessment",
    ratingLabels: ["Very bad", "Bad", "Neutral", "Good", "Very good"],
    whatFeelsWrong: "What feels wrong?",
    selectAll: "Select all that apply",
    aiProposal: "AI change proposal",
    understood: "What I understood",
    proposals: {
      floor: "Favor simple, continuous stone tiles.", entrances: "Keep exactly one primary entrance.", improvePrefix: "Improve readability:",
      heroLock: "Keep the current hero placement locked.", applyTo: "Apply to:",
    },
    hardConstraints: "Hard constraints",
    entranceConstraint: "Entrance = 1",
    safetyPreserved: "Safety preserved",
    purposeRequired: "Purpose required",
    weightChanges: "Weight changes",
    weights: { floorContinuity: "Floor continuity", specialTileRarity: "Special tile rarity", negativeSpace: "Negative space", storyEvidence: "Story evidence" },
    confidence: "Confidence",
    generating: "Generating…",
    regenerate: "Regenerate with changes",
    keepCurrent: "Keep current",
    developer: "Developer · Weights & signals",
    seed: "Seed 240711",
    previewOnly: "Preview only · Scene Apply unavailable",
    notices: {
      ready: "AI proposal ready", updated: "Interpretation updated", feedback: "Feedback added to proposal",
      generating: "Generating deterministic preview…", regenerated: "Preview regenerated · technical errors 0",
      reset: "Art direction reset", kept: "Current preview kept",
    },
  },
};

function DirectionSlider({ item, copy, onChange, onImportance }) {
  const [left, right] = copy.directions[item.id];
  return (
    <div className="direction-control">
      <div className="direction-labels"><span>{left}</span><span>{right}</span></div>
      <input
        aria-label={`${left} - ${right}`}
        type="range"
        min="0"
        max="100"
        value={item.value}
        onChange={(event) => onChange(Number(event.target.value))}
      />
      <button className="importance" onClick={onImportance} aria-label={`${copy.importance[item.importance]}`}>
        {copy.importance[item.importance]}<CaretDown size={12} weight="bold" />
      </button>
    </div>
  );
}

function AppHeader({ copy, locale, setLocale, scope, setScope, theme, setTheme, workspace, setWorkspace }) {
  const spatial = spatialTranslations[locale];
  return (
    <>
      <header className="app-titlebar">
        <div className="brand"><Cube size={19} weight="fill" /><span>{copy.brand}</span></div>
        <div className="title-tabs">
          <span>{copy.prototype}</span>
          <button className={workspace === "review" ? "active" : ""} onClick={() => setWorkspace("review")}>{copy.studio}</button>
          <button className={workspace === "spatial" ? "active" : ""} onClick={() => setWorkspace("spatial")}>{spatial.studio}</button>
        </div>
        <span className="preview-label">{copy.interactive}</span>
      </header>
      <div className="workspace-toolbar">
        <label>{copy.themeLabel}
          <select value={theme} onChange={(event) => setTheme(event.target.value)}>
            {Object.entries(copy.themes).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </label>
        <span className="toolbar-divider" />
        <label>{copy.scopeLabel}
          <select value={scope} onChange={(event) => setScope(event.target.value)}>
            {Object.entries(copy.scopes).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </label>
        <span className="toolbar-divider" />
        <label>{copy.languageLabel}
          <select value={locale} onChange={(event) => setLocale(event.target.value)} aria-label={copy.languageLabel}>
            <option value="ko">한국어</option><option value="en">English</option>
          </select>
        </label>
        <div className="safety"><ShieldCheck size={18} weight="fill" /><span>{copy.safety}</span><strong>{copy.locked}</strong><LockKey size={14} /></div>
      </div>
    </>
  );
}

function GeometryStudio({ locale }) {
  const copy = spatialTranslations[locale];
  const [selectedId, setSelectedId] = useState("banner");
  const [view, setView] = useState("front");
  const [showOverlay, setShowOverlay] = useState(true);
  const [selectedIssues, setSelectedIssues] = useState(new Set());
  const [comment, setComment] = useState("");
  const [decisions, setDecisions] = useState({});
  const [notice, setNotice] = useState(copy.latest);
  const [bridge, setBridge] = useState({ connected: false, nonce: "" });
  const [liveSession, setLiveSession] = useState(null);
  const [liveValidation, setLiveValidation] = useState(null);
  const baseSelected = calibrationCases.find((item) => item.id === selectedId) ?? calibrationCases[0];
  const liveContact = liveValidation?.contacts?.[0];
  const selected = baseSelected.id === "banner" && liveSession ? {
    ...baseSelected,
    name: liveSession.subjectName || baseSelected.name,
    relation: liveSession.template || baseSelected.relation,
    errors: liveValidation?.error_count ?? baseSelected.errors,
    gap: liveContact ? `${liveContact.gap.toFixed(4)} m` : baseSelected.gap,
    penetration: liveContact ? `${liveContact.penetration.toFixed(4)} m` : baseSelected.penetration,
    support: liveContact ? `${Math.round(liveContact.support * 100)}%` : baseSelected.support,
    direction: liveContact ? liveContact.direction_alignment.toFixed(3) : baseSelected.direction,
    hash: liveSession.captureSetHash ? `${liveSession.captureSetHash.slice(0, 4)}…${liveSession.captureSetHash.slice(-4)}` : baseSelected.hash,
  } : baseSelected;
  const liveCapture = selected.id === "banner" && Boolean(liveSession?.captureSetHash);
  const decision = decisions[selected.id] ?? selected.state;

  useEffect(() => {
    let active = true;
    fetch("http://127.0.0.1:4174/api/health")
      .then((response) => response.ok ? response.json() : Promise.reject(new Error("bridge unavailable")))
      .then(async (result) => {
        if (active) setBridge({ connected: Boolean(result.connected), nonce: result.nonce || "" });
        if (!result.connected) return;
        const response = await fetch("http://127.0.0.1:4174/api/spatial/session");
        if (!response.ok) throw new Error("calibration unavailable");
        const data = await response.json();
        if (active) { setLiveSession(data.session); setLiveValidation(data.validation); }
      })
      .catch(() => { if (active) setBridge({ connected: false, nonce: "" }); });
    return () => { active = false; };
  }, []);

  const chooseCase = (id) => {
    setSelectedId(id);
    setSelectedIssues(new Set());
    setComment("");
    setNotice(copy.latest);
  };

  const toggleIssue = (issue) => setSelectedIssues((current) => {
    const next = new Set(current);
    next.has(issue) ? next.delete(issue) : next.add(issue);
    return next;
  });

  const decide = async (next) => {
    if (next === "approved" && selected.errors > 0) return;
    setDecisions((current) => ({ ...current, [selected.id]: next }));
    if (!bridge.connected || !liveCapture) {
      setNotice(copy.prototypeSaved);
      return;
    }
    window.open("http://127.0.0.1:4174/workflow", "_blank", "noopener,noreferrer");
    setNotice(copy.saved);
  };

  const statusLabel = (item) => {
    const state = decisions[item.id] ?? item.state;
    if (state === "approved") return copy.approved;
    if (state === "revision") return copy.revision;
    if (state === "unable") return copy.unable;
    return item.errors ? `${copy.failed} ${item.errors}` : copy.pending;
  };

  return (
    <section className="spatial-studio">
      <aside className="calibration-queue">
        <div className="panel-title"><ListChecks size={18} />{copy.queue}</div>
        <div className="calibration-case-list">
          {calibrationCases.map((item) => (
            <button key={item.id} className={`calibration-case ${selected.id === item.id ? "selected" : ""}`} onClick={() => chooseCase(item.id)}>
              <span className="asset-thumb"><Cube size={24} weight="duotone" /></span>
              <span><strong>{item.name}</strong><small>{item.relation}</small></span>
              <em className={item.errors ? "error" : (decisions[item.id] === "approved" ? "approved" : "")}>{statusLabel(item)}</em>
            </button>
          ))}
        </div>
        <div className="queue-help"><ShieldCheck size={18} /><span>{copy.queueHelp}</span></div>
      </aside>

      <section className="evidence-workspace">
        <header className="evidence-header">
          <div><span>{copy.calibration}</span><strong>{selected.name}</strong></div>
          <span className={`gate-pill ${selected.errors ? "error" : "passed"}`}><ShieldCheck size={14} weight="fill" />{copy.technical}: {selected.errors ? `${copy.failed} ${selected.errors}` : copy.passed}</span>
        </header>
        <div className="evidence-toolbar">
          <strong>{copy.evidence}</strong>
          <div className="segmented">
            <button className={!showOverlay ? "active" : ""} onClick={() => setShowOverlay(false)}>{copy.raw}</button>
            <button className={showOverlay ? "active" : ""} onClick={() => setShowOverlay(true)}>{copy.overlay}</button>
          </div>
          <span>{liveCapture ? `${copy.liveCapture} · ` : ""}{copy.captureHash}: <code>{selected.hash}</code></span>
        </div>
        <div className="capture-stage">
          <div className={`capture-image view-${view} ${liveCapture ? "live-capture" : ""}`}>
            <img src={liveCapture ? `http://127.0.0.1:4174/api/spatial/image/${view}?evidence=${showOverlay ? 1 : 0}&hash=${liveSession.captureSetHash}` : "/assets/spatial-calibration-dungeon.png"} alt={`${selected.name} ${copy.views[view]}`} />
            {showOverlay && !liveCapture && <div className="evidence-overlay" aria-label={copy.overlay}>
              <span className="obb-box" /><span className="contact-axis axis-x" /><span className="contact-axis axis-y" />
              <em>{selected.frames}</em>
            </div>}
          </div>
          <nav className="capture-tabs">
            {Object.entries(copy.views).map(([id, label]) => <button key={id} className={view === id ? "active" : ""} onClick={() => setView(id)}><ImageSquare size={15} />{label}</button>)}
          </nav>
        </div>
        <div className="measurement-strip">
          <div><span>{copy.gap}</span><strong className={selected.id === "bottle" ? "bad" : ""}>{selected.gap}</strong><small>{copy.allowed}: {selected.relation === "WallMounted" ? "0.005–0.010 m" : selected.id === "bookshelf" ? "0.010–0.050 m" : "0–0.010 m"}</small></div>
          <div><span>{copy.penetration}</span><strong>{selected.penetration}</strong><small>{copy.allowed}: 0 m</small></div>
          <div><span>{copy.support}</span><strong className={selected.id === "bottle" ? "bad" : ""}>{selected.support}</strong><small>{copy.allowed}: ≥ 60%</small></div>
          <div><span>{copy.direction}</span><strong>{selected.direction}</strong><small>{copy.allowed}: ≥ 0.95</small></div>
        </div>
      </section>

      <aside className="human-review-panel">
        <div className="panel-title"><CheckCircle size={18} weight="fill" />{copy.human}</div>
        <div className="contract-summary">
          <div><span>{copy.contract}</span><strong>{statusLabel(selected)}</strong></div>
          <dl><div><dt>{copy.relation}</dt><dd>{selected.relation}</dd></div><div><dt>{copy.frames}</dt><dd>{selected.frames}</dd></div></dl>
        </div>
        <div className="ai-boundary"><Brain size={18} weight="fill" /><div><strong>{copy.aiTitle}</strong><p>{copy.aiBody}</p></div></div>
        <div className="review-form">
          <strong>{copy.issuePrompt}</strong>
          <div className="spatial-issues">{copy.issues.map((issue) => <button key={issue} className={selectedIssues.has(issue) ? "selected" : ""} onClick={() => toggleIssue(issue)}>{selectedIssues.has(issue) ? <WarningCircle size={14} weight="fill" /> : <CheckCircle size={14} />}{issue}</button>)}</div>
          <textarea value={comment} onChange={(event) => setComment(event.target.value)} placeholder={copy.comment} />
        </div>
        {selected.errors > 0 && <div className="approval-blocked"><WarningCircle size={16} weight="fill" />{copy.approvalBlocked}</div>}
        <div className="decision-actions">
          <button className="approve-action" disabled={selected.errors > 0} onClick={() => decide("approved")}><Check size={16} weight="bold" />{copy.approve}</button>
          <button onClick={() => decide("revision")}><WarningCircle size={16} />{copy.request}</button>
          <button onClick={() => decide("unable")}><ImageSquare size={16} />{copy.cannotJudge}</button>
        </div>
        {decision === "unable" && <button className="recapture-action" onClick={() => setNotice(copy.latest)}><ArrowClockwise size={15} />{copy.recapture}</button>}
        <div className="reviewer-note"><LockKey size={14} />{copy.reviewer}<em className={bridge.connected ? "connected" : ""}>{bridge.connected ? copy.bridgeOn : copy.bridgeOff}</em></div>
      </aside>
      <div className="spatial-status"><CheckCircle size={14} weight="fill" />{notice}</div>
    </section>
  );
}

function App() {
  const [locale, setLocale] = useState("ko");
  const [workspace, setWorkspace] = useState("spatial");
  const copy = translations[locale];
  const [directions, setDirections] = useState(directionDefaults);
  const [activeId, setActiveId] = useState("floor");
  const [ratings, setRatings] = useState(Object.fromEntries(categories.map((item) => [item.id, item.score])));
  const [selectedIssues, setSelectedIssues] = useState({ floor: new Set(["specialTiles"]) });
  const [scope, setScope] = useState("room");
  const [theme, setTheme] = useState("dungeon");
  const [developerOpen, setDeveloperOpen] = useState(false);
  const [isGenerating, setIsGenerating] = useState(false);
  const [regenerated, setRegenerated] = useState(false);
  const [noticeKey, setNoticeKey] = useState("ready");

  const active = categories.find((item) => item.id === activeId) ?? categories[1];
  const activeCopy = copy.categories[active.id];
  const activeRating = ratings[active.id] ?? active.score;
  const currentIssues = selectedIssues[active.id] ?? new Set();

  const weightChanges = useMemo(() => {
    const byId = Object.fromEntries(directions.map((item) => [item.id, item.value]));
    return [
      ["floorContinuity", Math.round((100 - byId.variation) * 0.16)],
      ["specialTileRarity", -Math.round(byId.variation * 0.12)],
      ["negativeSpace", Math.round((100 - byId.density) * 0.1)],
      ["storyEvidence", Math.round(byId.story * 0.12)],
    ];
  }, [directions]);

  const updateDirection = (id, patch) => {
    setDirections((items) => items.map((item) => item.id === id ? { ...item, ...patch } : item));
    setNoticeKey("updated");
  };

  const cycleImportance = (item) => {
    const values = ["low", "medium", "high", "must"];
    const next = values[(values.indexOf(item.importance) + 1) % values.length];
    updateDirection(item.id, { importance: next });
  };

  const toggleIssue = (issue) => {
    setSelectedIssues((current) => {
      const next = new Set(current[active.id] ?? []);
      if (next.has(issue)) next.delete(issue); else next.add(issue);
      return { ...current, [active.id]: next };
    });
    setNoticeKey("feedback");
  };

  const regenerate = () => {
    setIsGenerating(true);
    setNoticeKey("generating");
    window.setTimeout(() => {
      setIsGenerating(false);
      setRegenerated(true);
      setNoticeKey("regenerated");
    }, 900);
  };

  const reset = () => {
    setDirections(directionDefaults);
    setNoticeKey("reset");
  };

  const categoryStatus = (score) => score <= 2 ? copy.statuses.needsWork : score === 3 ? copy.statuses.neutral : copy.statuses.good;

  return (
    <main className={`unity-shell ${workspace === "spatial" ? "spatial-shell" : ""}`} lang={locale}>
      <AppHeader copy={copy} locale={locale} setLocale={setLocale} scope={scope} setScope={setScope} theme={theme} setTheme={setTheme} workspace={workspace} setWorkspace={setWorkspace} />

      {workspace === "review" ? <>
      <section className="direction-band">
        <div className="direction-heading">
          <SlidersHorizontal size={20} weight="fill" />
          <div><strong>{copy.artDirection}</strong><span>{copy.artDirectionHelp}</span></div>
          <button className="icon-text" onClick={reset}><ArrowClockwise size={15} />{copy.reset}</button>
        </div>
        <div className="direction-grid">
          {directions.map((item) => (
            <DirectionSlider key={item.id} item={item} copy={copy} onChange={(value) => updateDirection(item.id, { value })} onImportance={() => cycleImportance(item)} />
          ))}
        </div>
      </section>

      <section className="studio-grid">
        <aside className="review-nav">
          <div className="panel-title">{copy.reviewCategories}</div>
          <div className="category-list">
            {categories.map((item) => {
              const Icon = item.icon;
              const score = ratings[item.id] ?? item.score;
              return (
                <button key={item.id} className={`category ${activeId === item.id ? "selected" : ""}`} onClick={() => setActiveId(item.id)} aria-label={`${copy.categories[item.id].label} ${categoryStatus(score)}`}>
                  <Icon size={21} />
                  <span>{copy.categories[item.id].label}</span>
                  <em className={`score score-${score}`}>{categoryStatus(score)}</em>
                </button>
              );
            })}
          </div>
          <div className="nav-help"><ListChecks size={18} /><span>{copy.categoryHelp}</span></div>
        </aside>

        <section className="comparison-panel">
          <div className="comparison-images">
            <figure>
              <figcaption><span>{copy.before}</span><small>{copy.currentPreview}</small></figcaption>
              <div className="image-frame"><img src="/assets/before-dungeon.png" alt={copy.beforeAlt} /></div>
            </figure>
            <figure className={regenerated ? "accepted" : ""}>
              <figcaption><span>{copy.regenerated}</span><small>{regenerated ? copy.updatedFromFeedback : copy.proposedPreview}</small></figcaption>
              <div className="image-frame"><img src="/assets/after-dungeon.png" alt={copy.afterAlt} />{isGenerating && <div className="generating"><Sparkle size={24} weight="fill" />{copy.generatingPreview}</div>}</div>
            </figure>
          </div>

          <div className="critique-form">
            <div className="critique-heading"><div><span>{copy.assessment}</span><strong>{activeCopy.label}</strong></div><p>{activeCopy.summary}</p></div>
            <div className="rating-row" role="radiogroup" aria-label={`${activeCopy.label} ${copy.assessment}`}>
              {copy.ratingLabels.map((label, index) => {
                const value = index + 1;
                return <button key={label} className={activeRating === value ? "active" : ""} onClick={() => setRatings((current) => ({ ...current, [active.id]: value }))} aria-label={`${value} ${label}`}><b>{value}</b><span>{label}</span></button>;
              })}
            </div>
            <div className="issues-title">{copy.whatFeelsWrong} <span>{copy.selectAll}</span></div>
            <div className="issue-chips">
              {active.issues.map((issue) => <button key={issue} className={currentIssues.has(issue) ? "selected" : ""} onClick={() => toggleIssue(issue)}>{currentIssues.has(issue) ? <WarningCircle size={15} weight="fill" /> : <CheckCircle size={15} />}{copy.issues[issue]}</button>)}
            </div>
          </div>
        </section>

        <aside className="proposal-panel">
          <div className="panel-title"><Brain size={18} weight="fill" />{copy.aiProposal}</div>
          <div className="proposal-copy"><strong>{copy.understood}</strong><p>{activeCopy.summary}</p></div>
          <div className="proposal-list">
            <div><Check size={15} weight="bold" /><span>{active.id === "floor" ? copy.proposals.floor : `${copy.proposals.improvePrefix} ${activeCopy.label}`}</span></div>
            <div><Check size={15} weight="bold" /><span>{active.id === "entrances" ? copy.proposals.entrances : copy.proposals.heroLock}</span></div>
            <div><Check size={15} weight="bold" /><span>{copy.proposals.applyTo} {copy.scopes[scope]}</span></div>
          </div>
          <div className="change-columns">
            <div><span>{copy.hardConstraints}</span><strong>{active.id === "entrances" ? copy.entranceConstraint : copy.safetyPreserved}</strong><strong>{copy.purposeRequired}</strong></div>
            <div><span>{copy.weightChanges}</span>{weightChanges.slice(0, 3).map(([name, value]) => <strong key={name}>{copy.weights[name]}<em className={value >= 0 ? "positive" : "negative"}>{value >= 0 ? "+" : ""}{value}</em></strong>)}</div>
          </div>
          <div className="confidence"><span>{copy.confidence}</span><strong>91%</strong><div><i style={{ width: "91%" }} /></div></div>
          <button className="primary-action" onClick={regenerate} disabled={isGenerating}><Sparkle size={17} weight="fill" />{isGenerating ? copy.generating : copy.regenerate}</button>
          <button className="secondary-action" onClick={() => { setRegenerated(false); setNoticeKey("kept"); }}><Check size={17} />{copy.keepCurrent}</button>
          <button className="developer-toggle" onClick={() => setDeveloperOpen((open) => !open)}>{copy.developer}<CaretDown className={developerOpen ? "open" : ""} size={14} /></button>
          {developerOpen && <div className="developer-values">{weightChanges.map(([name, value]) => <div key={name}><span>{copy.weights[name]}</span><code>{value >= 0 ? "+" : ""}{value}</code></div>)}</div>}
        </aside>
      </section>

      <footer className="statusbar"><span className="status-ok"><CheckCircle size={15} weight="fill" />{copy.notices[noticeKey]}</span><span>{copy.seed}</span><span>{copy.previewOnly}</span></footer>
      </> : <GeometryStudio locale={locale} />}
    </main>
  );
}

export { App };
