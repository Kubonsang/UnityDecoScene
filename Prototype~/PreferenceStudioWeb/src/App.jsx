import { useMemo, useState } from "react";
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

const directionDefaults = [
  { id: "order", left: "Ordered", right: "Chaotic", value: 25, importance: "High" },
  { id: "density", left: "Sparse", right: "Dense", value: 43, importance: "Medium" },
  { id: "variation", left: "Uniform", right: "Varied", value: 28, importance: "High" },
  { id: "damage", left: "Intact", right: "Ruined", value: 37, importance: "Medium" },
  { id: "symmetry", left: "Symmetric", right: "Asymmetric", value: 32, importance: "Low" },
  { id: "drama", left: "Functional", right: "Dramatic", value: 61, importance: "High" },
  { id: "story", left: "Subtle", right: "Story-rich", value: 67, importance: "High" },
];

const categories = [
  {
    id: "structure",
    label: "Structure",
    icon: SquaresFour,
    score: 4,
    summary: "The room shell reads clearly and keeps the focal area open.",
    issues: ["Room purpose unclear", "Too many partitions", "Weak focal hierarchy", "Unused corners"],
  },
  {
    id: "floor",
    label: "Floor",
    icon: GridFour,
    score: 1,
    summary: "The floor uses special tiles without a clear function and creates visual noise.",
    issues: ["Meaningless special tiles", "Too repetitive", "Breaks walkway", "Too much damage"],
  },
  {
    id: "entrances",
    label: "Entrances",
    icon: DoorOpen,
    score: 2,
    summary: "Two adjacent entrances compete for attention and make the room's approach ambiguous.",
    issues: ["Too many entrances", "Weak arrival view", "No clear main door", "Blocks focal view"],
  },
  {
    id: "walls",
    label: "Wall Dressing",
    icon: ImageSquare,
    score: 2,
    summary: "Wall dressing needs clearer mounting zones and more distance from corners and pillars.",
    issues: ["Too close to corner", "Conflicts with pillar", "Wrong height", "Uneven visual rhythm"],
  },
  {
    id: "props",
    label: "Props",
    icon: Cube,
    score: 4,
    summary: "The hero and supporting props form a readable cluster with useful negative space.",
    issues: ["Hero is obscured", "Relationships unclear", "Too much clutter", "Empty supporting zone"],
  },
  {
    id: "lighting",
    label: "Lighting",
    icon: Lightbulb,
    score: 4,
    summary: "Warm focal light and cool fill create a strong dungeon mood without losing readability.",
    issues: ["Too flat", "Overexposed focal area", "Hero too dark", "Color contrast too strong"],
  },
];

const ratingLabels = ["Very bad", "Bad", "Neutral", "Good", "Very good"];

function DirectionSlider({ item, onChange, onImportance }) {
  return (
    <div className="direction-control">
      <div className="direction-labels">
        <span>{item.left}</span>
        <span>{item.right}</span>
      </div>
      <input
        aria-label={`${item.left} to ${item.right}`}
        type="range"
        min="0"
        max="100"
        value={item.value}
        onChange={(event) => onChange(Number(event.target.value))}
      />
      <button className="importance" onClick={onImportance} aria-label={`Importance ${item.importance}`}>
        {item.importance}<CaretDown size={12} weight="bold" />
      </button>
    </div>
  );
}

function AppHeader({ scope, setScope, theme, setTheme }) {
  return (
    <>
      <header className="app-titlebar">
        <div className="brand"><Cube size={19} weight="fill" /><span>Concept Room Decorator</span></div>
        <div className="title-tabs"><span>Preference Prototype</span><span className="active">Review Studio</span></div>
        <span className="preview-label">INTERACTIVE PROTOTYPE</span>
      </header>
      <div className="workspace-toolbar">
        <label>Theme adapter
          <select value={theme} onChange={(event) => setTheme(event.target.value)}>
            <option>Dungeon</option><option>Temple</option><option>Sci-Fi Lab</option><option>Tavern</option>
          </select>
        </label>
        <span className="toolbar-divider" />
        <label>Feedback scope
          <select value={scope} onChange={(event) => setScope(event.target.value)}>
            <option>This room only</option><option>Dungeon theme</option><option>Project preference</option>
          </select>
        </label>
        <div className="safety"><ShieldCheck size={18} weight="fill" /><span>Technical safety</span><strong>Locked</strong><LockKey size={14} /></div>
      </div>
    </>
  );
}

function App() {
  const [directions, setDirections] = useState(directionDefaults);
  const [activeId, setActiveId] = useState("floor");
  const [ratings, setRatings] = useState(Object.fromEntries(categories.map((item) => [item.id, item.score])));
  const [selectedIssues, setSelectedIssues] = useState({ floor: new Set(["Meaningless special tiles"]) });
  const [scope, setScope] = useState("This room only");
  const [theme, setTheme] = useState("Dungeon");
  const [developerOpen, setDeveloperOpen] = useState(false);
  const [isGenerating, setIsGenerating] = useState(false);
  const [regenerated, setRegenerated] = useState(false);
  const [notice, setNotice] = useState("AI proposal ready");

  const active = categories.find((item) => item.id === activeId) ?? categories[1];
  const activeRating = ratings[active.id] ?? active.score;
  const currentIssues = selectedIssues[active.id] ?? new Set();

  const weightChanges = useMemo(() => {
    const byId = Object.fromEntries(directions.map((item) => [item.id, item.value]));
    return [
      ["Floor continuity", Math.round((100 - byId.variation) * 0.16)],
      ["Special tile rarity", -Math.round(byId.variation * 0.12)],
      ["Negative space", Math.round((100 - byId.density) * 0.1)],
      ["Story evidence", Math.round(byId.story * 0.12)],
    ];
  }, [directions]);

  const updateDirection = (id, patch) => {
    setDirections((items) => items.map((item) => item.id === id ? { ...item, ...patch } : item));
    setNotice("Interpretation updated");
  };

  const cycleImportance = (item) => {
    const values = ["Low", "Medium", "High", "Must"];
    const next = values[(values.indexOf(item.importance) + 1) % values.length];
    updateDirection(item.id, { importance: next });
  };

  const toggleIssue = (issue) => {
    setSelectedIssues((current) => {
      const next = new Set(current[active.id] ?? []);
      if (next.has(issue)) next.delete(issue); else next.add(issue);
      return { ...current, [active.id]: next };
    });
    setNotice("Feedback added to proposal");
  };

  const regenerate = () => {
    setIsGenerating(true);
    setNotice("Generating deterministic preview…");
    window.setTimeout(() => {
      setIsGenerating(false);
      setRegenerated(true);
      setNotice("Preview regenerated · technical errors 0");
    }, 900);
  };

  const reset = () => {
    setDirections(directionDefaults);
    setNotice("Art direction reset");
  };

  return (
    <main className="unity-shell">
      <AppHeader scope={scope} setScope={setScope} theme={theme} setTheme={setTheme} />

      <section className="direction-band">
        <div className="direction-heading">
          <SlidersHorizontal size={20} weight="fill" />
          <div><strong>Art Direction</strong><span>Guide the result before you critique it</span></div>
          <button className="icon-text" onClick={reset}><ArrowClockwise size={15} />Reset</button>
        </div>
        <div className="direction-grid">
          {directions.map((item) => (
            <DirectionSlider
              key={item.id}
              item={item}
              onChange={(value) => updateDirection(item.id, { value })}
              onImportance={() => cycleImportance(item)}
            />
          ))}
        </div>
      </section>

      <section className="studio-grid">
        <aside className="review-nav">
          <div className="panel-title">Review categories</div>
          <div className="category-list">
            {categories.map((item) => {
              const Icon = item.icon;
              const score = ratings[item.id] ?? item.score;
              return (
                <button key={item.id} className={`category ${activeId === item.id ? "selected" : ""}`} onClick={() => setActiveId(item.id)}>
                  <Icon size={21} />
                  <span>{item.label}</span>
                  <em className={`score score-${score}`}>{score <= 2 ? "Needs work" : score === 3 ? "Neutral" : "Good"}</em>
                </button>
              );
            })}
          </div>
          <div className="nav-help"><ListChecks size={18} /><span>Select a category to focus the critique and AI proposal.</span></div>
        </aside>

        <section className="comparison-panel">
          <div className="comparison-images">
            <figure>
              <figcaption><span>Before</span><small>Current preview</small></figcaption>
              <div className="image-frame"><img src="/assets/before-dungeon.png" alt="Current dungeon room preview with noisy floor patterns" /></div>
            </figure>
            <figure className={regenerated ? "accepted" : ""}>
              <figcaption><span>Regenerated</span><small>{regenerated ? "Updated from feedback" : "Proposed preview"}</small></figcaption>
              <div className="image-frame"><img src="/assets/after-dungeon.png" alt="Regenerated dungeon room preview with clearer visual hierarchy" />{isGenerating && <div className="generating"><Sparkle size={24} weight="fill" />Generating preview…</div>}</div>
            </figure>
          </div>

          <div className="critique-form">
            <div className="critique-heading"><div><span>Your assessment</span><strong>{active.label}</strong></div><p>{active.summary}</p></div>
            <div className="rating-row" role="radiogroup" aria-label={`${active.label} rating`}>
              {ratingLabels.map((label, index) => {
                const value = index + 1;
                return <button key={label} className={activeRating === value ? "active" : ""} onClick={() => setRatings((current) => ({ ...current, [active.id]: value }))}><b>{value}</b><span>{label}</span></button>;
              })}
            </div>
            <div className="issues-title">What feels wrong? <span>Select all that apply</span></div>
            <div className="issue-chips">
              {active.issues.map((issue) => <button key={issue} className={currentIssues.has(issue) ? "selected" : ""} onClick={() => toggleIssue(issue)}>{currentIssues.has(issue) ? <WarningCircle size={15} weight="fill" /> : <CheckCircle size={15} />}{issue}</button>)}
            </div>
          </div>
        </section>

        <aside className="proposal-panel">
          <div className="panel-title"><Brain size={18} weight="fill" />AI change proposal</div>
          <div className="proposal-copy"><strong>What I understood</strong><p>{active.summary}</p></div>
          <div className="proposal-list">
            <div><Check size={15} weight="bold" /><span>{active.id === "floor" ? "Favor simple, continuous stone tiles." : `Improve ${active.label.toLowerCase()} readability.`}</span></div>
            <div><Check size={15} weight="bold" /><span>{active.id === "entrances" ? "Keep exactly one primary entrance." : "Keep the current hero placement locked."}</span></div>
            <div><Check size={15} weight="bold" /><span>Apply to: {scope}.</span></div>
          </div>
          <div className="change-columns">
            <div><span>Hard constraints</span><strong>{active.id === "entrances" ? "Entrance = 1" : "Safety preserved"}</strong><strong>Purpose required</strong></div>
            <div><span>Weight changes</span>{weightChanges.slice(0, 3).map(([name, value]) => <strong key={name}>{name}<em className={value >= 0 ? "positive" : "negative"}>{value >= 0 ? "+" : ""}{value}</em></strong>)}</div>
          </div>
          <div className="confidence"><span>Confidence</span><strong>91%</strong><div><i style={{ width: "91%" }} /></div></div>
          <button className="primary-action" onClick={regenerate} disabled={isGenerating}><Sparkle size={17} weight="fill" />{isGenerating ? "Generating…" : "Regenerate with changes"}</button>
          <button className="secondary-action" onClick={() => { setRegenerated(false); setNotice("Current preview kept"); }}><Check size={17} />Keep current</button>
          <button className="developer-toggle" onClick={() => setDeveloperOpen((open) => !open)}>Developer · Weights & signals<CaretDown className={developerOpen ? "open" : ""} size={14} /></button>
          {developerOpen && <div className="developer-values">{weightChanges.map(([name, value]) => <div key={name}><span>{name}</span><code>{value >= 0 ? "+" : ""}{value}</code></div>)}</div>}
        </aside>
      </section>

      <footer className="statusbar"><span className="status-ok"><CheckCircle size={15} weight="fill" />{notice}</span><span>Seed 240711</span><span>Preview only · Scene Apply unavailable</span></footer>
    </main>
  );
}

export { App };
