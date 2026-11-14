"use strict";
(() => {
  const KEYS = ["food", "funds", "stone", "timber", "armaments"];
  const LABEL = { food: "Food", funds: "Funds", stone: "Stone", timber: "Timber", armaments: "Armaments" };
  const ROLE_OTHER = "Other";
  const ICON = {
    food: '<path d="M12 2c1.2 1.4 1.2 3.2 0 4.6-1.2-1.4-1.2-3.2 0-4.6Zm-4 3.5c1.7.4 2.7 1.9 2.6 3.7-1.7-.4-2.7-1.9-2.6-3.7Zm8 0c.1 1.8-.9 3.3-2.6 3.7-.1-1.8.9-3.3 2.6-3.7ZM7.5 10c1.8.3 3 1.8 3 3.6-1.8-.3-3-1.8-3-3.6Zm9 0c0 1.8-1.2 3.3-3 3.6 0-1.8 1.2-3.3 3-3.6ZM11.2 9h1.6v13h-1.6Z"/>',
    funds: '<ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 8.5c0 1.7 3.1 3 7 3s7-1.3 7-3V11c0 1.7-3.1 3-7 3s-7-1.3-7-3Zm0 5c0 1.7 3.1 3 7 3s7-1.3 7-3V16c0 1.7-3.1 3-7 3s-7-1.3-7-3Z"/>',
    stone: '<path d="M4 15 7 7l6-3 6 4 2 7-5 5H8Zm4.2-6.5L6.4 14l2.3 3.8h6.4l3.6-3.6-1.4-4.9-4.4-2.9Z"/>',
    timber: '<path d="M3 9.5C3 8.1 4.1 7 5.5 7H18v10H5.5A2.5 2.5 0 0 1 3 14.5Z" opacity=".55"/><circle cx="18" cy="12" r="5"/><circle cx="18" cy="12" r="2.2" fill="var(--panel)"/>',
    armaments: '<path d="m4 3 9.5 9.5-1.5 1.5L2.5 4.5V3Zm16 0v1.5L13.4 11l-1.5-1.5L18.5 3ZM6 15l3 3-2 2-1-1-2 2-1-1 2-2-1-1Zm12 0 2 2-1 1 2 2-1 1-2-2-1 1-2-2Z"/>',
  };
  const icon = (k, s = 16) => `<svg viewBox="0 0 24 24" width="${s}" height="${s}" fill="currentColor" aria-hidden="true">${ICON[k] || ""}</svg>`;

  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const nf = new Intl.NumberFormat("en-US");
  const fmt = (n) => (n == null ? '<span class="unk">unknown</span>' : nf.format(Math.round(n)));
  const rate = (n, unit = "/h") => {
    if (n == null) return '<span class="unk">unknown</span>';
    const a = Math.abs(n), v = a >= 100 ? Math.round(n) : Math.round(n * 10) / 10;
    const cls = n > 0.05 ? "pos" : n < -0.05 ? "neg" : "zero";
    return `<b class="${cls}">${n > 0 ? "+" : ""}${nf.format(v)}</b>${unit}`;
  };
  const hours = (h) => {
    if (h == null) return "unknown";
    if (h < 1) return `${Math.round(h * 60)} min`;
    if (h < 48) return `${Math.round(h * 10) / 10} h`;
    return `${Math.round(h / 24 * 10) / 10} days`;
  };
  const store = {
    get(k, d) { try { const v = localStorage.getItem("cl." + k); return v == null ? d : JSON.parse(v); } catch { return d; } },
    set(k, v) { try { localStorage.setItem("cl." + k, JSON.stringify(v)); } catch { /* private mode */ } },
  };

  const ui = {
    tab: store.get("tab", "missions"),
    q: "", region: "", role: store.get("role", "all"), fits: store.get("fits", false), sort: store.get("sort", "net"),
    open: new Set(),
    hist: store.get("hist", "funds"),
    plan: store.get("plan", { food: 0, funds: 0, stone: 0, timber: 0, armaments: 0 }),
    simAdd: store.get("simAdd", []),      // [{ op, comrades: [characterKey] }]
    simStop: store.get("simStop", []),    // [operationKey]
    simSwap: store.get("simSwap", []),    // [{ op, comrades: [characterKey] }]: running missions with a new crew
    keep: store.get("keep", {}),          // { characterKey: activity }: comrades kept on hand, never auto-picked
    optRunning: store.get("optRunning", true),
    cq: "", cskill: "", cstate: store.get("cstate", "all"),
  };
  let optUndo = null, optNotes = null;
  let sim = null, simTimer = 0, simSeq = 0;
  const TABS = ["missions", "available", "comrades", "plan", "planner", "history"];
  const SKILL_NAME = { Foodprocessing: "Cooking" };
  const skillName = (t) => SKILL_NAME[t] || String(t || "").replace(/([a-z])([A-Z])/g, "$1 $2");
  const ROMAN = ["", "I", "II", "III", "IV", "V"];
  const matchOf = (c, type) => !type || !c.slots ? 0 : c.slots.reduce((n, s) => n + (s && s.unlocked && s.type === type ? s.percent : 0), 0);
  const inPlan = (op) => ui.simAdd.some((a) => String(a.op) === String(op));
  const stopping = (op) => ui.simStop.some((x) => String(x) === String(op));
  const switching = (op) => ui.simSwap.some((s) => String(s.op) === String(op));
  const planSize = () => ui.simAdd.length + ui.simStop.length + ui.simSwap.length;
  const saveSim = () => { store.set("simAdd", ui.simAdd); store.set("simStop", ui.simStop); store.set("simSwap", ui.simSwap); queueSim(); };
  const keptFor = (k) => ui.keep[String(k)];
  const saveKeep = () => store.set("keep", ui.keep);
  // A comrade's skills as activity choices for "keep on hand", best first.
  const activitiesOf = (c) => [...new Set((c.slots || []).filter(Boolean).map((s) => s.type))]
    .sort((a, b) => matchOf(c, b) - matchOf(c, a) || skillName(a).localeCompare(skillName(b)));
  const encodeCrews = (list) => list.map((a) => `${a.op}:${a.comrades.join(".")}`).join(",");
  let view = null, etag = null, lastOk = 0, failing = false;

  // ---------- data ----------
  async function poll() {
    try {
      const res = await fetch("/api/state", { headers: etag ? { "If-None-Match": etag } : {}, cache: "no-store" });
      lastOk = Date.now(); failing = false;
      if (res.status === 200) {
        etag = res.headers.get("ETag");
        view = await res.json();
        render();
      } else if (res.status !== 304) throw new Error(res.status);
    } catch {
      failing = true;
    }
    renderStatus();
  }

  // ---------- header / status ----------
  function ago(t) {
    if (!t) return "never";
    const s = Math.max(0, (Date.now() - new Date(t).getTime()) / 1000);
    if (s < 60) return `${Math.round(s)}s ago`;
    if (s < 3600) return `${Math.round(s / 60)} min ago`;
    if (s < 86400) return `${Math.round(s / 3600)} h ago`;
    return new Date(t).toLocaleDateString();
  }
  function renderStatus() {
    const el = $("status");
    if (failing || !view) {
      el.className = "pill err";
      el.innerHTML = `<span class="dot"></span>${view ? "Reader stopped — showing last data" : "Reader not reachable"}`;
      return;
    }
    const st = view.status;
    const live = st.gameRunning && st.reservesSource === "process";
    el.className = "pill " + (st.gameRunning ? "live" : "off");
    el.innerHTML = `<span class="dot"></span>${st.gameRunning ? (live ? "Live" : "Game running") : "Game closed"} · updated ${ago(st.lastUpdate)}`;
    el.title = `Reserves from ${st.reservesSource === "process" ? "game memory" : "the last save"}; missions from the save read ${ago(st.saveReadAt)}. Game ${st.gameVersion ?? "version unknown"}.`;
  }

  function renderHeader() {
    const st = view.status, w = view.workers;
    const camp = $("camp");
    camp.hidden = st.campLevel == null;
    camp.textContent = `Camp level ${st.campLevel}`;
    const crew = $("crew");
    crew.hidden = w.total == null;
    crew.innerHTML = `${w.inUse} / ${w.total} comrades out` + (w.state === "OverAssigned" ? ' <span class="neg">over-assigned</span>' : "");
    crew.title = w.free == null ? "" : `${w.free} free`;
    const banner = $("banner");
    const low = view.reserves.filter((r) => r.operatingBudget && r.perCycle != null && r.perCycle < 0);
    banner.hidden = low.length === 0;
    banner.innerHTML = low.length ? `Operating budget is shrinking: ${low.map((r) => `<b>${LABEL[r.key]}</b> ${nf.format(r.perCycle)} per cycle`).join(", ")}.` : "";
  }

  // ---------- reserves ----------
  function renderReserves() {
    $("reserves").innerHTML = view.reserves.map((r) => {
      const neg = r.perHour != null && r.perHour < -0.05;
      const warn = r.hoursUntilEmpty != null ? `<div class="res-warn">Empty in about ${hours(r.hoursUntilEmpty)} of play</div>` : "";
      return `<article class="res${neg ? " neg" : ""}" title="Net per cycle: ${r.perCycle == null ? "unknown" : nf.format(r.perCycle)}">
        <div class="res-head">${icon(r.key)}<span>${LABEL[r.key]}</span>${r.operatingBudget ? '<span class="tag">BUDGET</span>' : ""}</div>
        <div class="res-bal">${fmt(r.balance)}</div>
        <div class="res-rate"><span>${rate(r.perHour)}</span><span>${rate(r.perDay, "/game day")}</span></div>${warn}
      </article>`;
    }).join("");
  }

  // ---------- amounts ----------
  function amounts(obj, sign = "") {
    if (!obj) return '<span class="unk">unknown</span>';
    const parts = KEYS.filter((k) => obj[k]).map((k) => `<span class="amt">${icon(k, 14)}${sign}${nf.format(obj[k])}</span>`);
    return parts.length ? `<span class="amts">${parts.join("")}</span>` : '<span class="zero">—</span>';
  }
  // A build (game role "Building") pays out no reserves: say so instead of a bare dash.
  const isBuild = (m) => m.role === "Building";
  function rewardCell(m, obj) {
    if (obj && isBuild(m) && KEYS.every((k) => !obj[k])) return '<span class="zero" title="Builds pay out no reserves">none (build)</span>';
    return amounts(obj);
  }
  function netList(perHour) {
    if (!perHour) return '<span class="unk">unknown</span>';
    const parts = KEYS.filter((k) => Math.abs(perHour[k]) > 0.0001).map((k) => `<span class="amt">${icon(k, 14)}${rate(perHour[k])}</span>`);
    return parts.length ? `<span class="amts">${parts.join("")}</span>` : '<span class="zero">—</span>';
  }
  const realDay = () => (view ? hours(24 * view.realSecondsPerGameHour / 3600) : "unknown");
  const realEta = (h) => (h == null ? "" : ` · ≈ ${hours(h * view.realSecondsPerGameHour / 3600)} real`);

  // ---------- missions ----------
  // The save's repeat toggle (_operationShouldRepeat). null = the save has no such field: say so, do not guess.
  function repeatTag(m) {
    if (m.repeat === true) return '<span class="repeat on" title="Repeat is on in the game: it restarts when it finishes">↻ Repeating</span>';
    if (m.repeat === false) return '<span class="repeat off" title="Repeat is off in the game: it ends when it finishes">One-off</span>';
    return '<span class="repeat unk" title="This save has no repeat setting for the mission">Repeat unknown</span>';
  }
  function missionCard(m) {
    const pct = m.progress == null ? 0 : Math.round(m.progress * 100);
    const b = m.bonus;
    const bonus = b ? `<div class="bonus">Bonus <b>${b.total == null ? "unknown" : "+" + b.total + "%"}</b>
        <span title="Church conversion · best comrade skill · extra comrades">(${b.conversion ?? "?"}% conversion · ${b.skill ?? "?"}% ${esc(b.skillType ?? "skill")} · ${b.comrade ?? "?"}% comrades)</span></div>` : "";
    const stop = m.operationKey != null && stopping(m.operationKey);
    const swap = m.operationKey != null && switching(m.operationKey);
    return `<article class="card${stop ? " stopping" : ""}${swap ? " switching" : ""}">
      <div class="card-head"><div><h3>${esc(m.location ?? m.name)}</h3><div class="type">${esc(m.type ?? "")}</div></div>
        <div class="card-tags">${repeatTag(m)}${m.region ? `<span class="region">${esc(m.region)}</span>` : ""}</div></div>
      <div><div class="bar" role="progressbar" aria-valuenow="${pct}" aria-valuemin="0" aria-valuemax="100"><i style="width:${pct}%"></i></div>
        <div class="bar-meta"><span>${m.elapsedHours == null ? "?" : Math.round(m.elapsedHours * 10) / 10} / ${m.durationHours ?? "?"} h in game</span>
        <span>${m.remainingHours == null ? "" : hours(m.remainingHours) + " left" + realEta(m.remainingHours)}</span></div></div>
      <div class="flow">
        <span class="lbl">Cost</span>${amounts(m.cost)}${m.itemCost ? `<span class="lbl">Items</span><span class="extra">${esc(m.itemCost)}</span>` : ""}
        <span class="lbl">Reward</span>${rewardCell(m, m.reward)}
        <span class="lbl">Net</span>${netList(m.netPerHour)}
      </div>
      ${bonus}
      ${m.comrades.length ? `<div class="comrades">${m.comrades.map((c) => `<span class="chip-s">${esc(c)}</span>`).join("")}</div>` : ""}
      ${m.personalStorageReward ? `<div class="extra">Also to storage: ${esc(m.personalStorageReward)}</div>` : ""}
      ${m.operationKey != null ? `<div class="card-foot">
        ${m.recurring && !stop ? `<button class="btn small${swap ? " on" : ""}" data-plan-swap="${esc(m.operationKey)}"
          title="Try this mission with different comrades">${swap ? "Switching crew · undo" : "Switch crew"}</button>` : ""}
        <button class="btn small danger${stop ? " on" : ""}" data-plan-stop="${esc(m.operationKey)}"
          title="Try the plan without this mission">${stop ? "Stopping in plan · undo" : "Stop in plan"}</button></div>` : ""}
    </article>`;
  }
  function renderMissions() {
    const rec = view.running.filter((m) => m.recurring), once = view.running.filter((m) => !m.recurring);
    let html = `<div class="section-title"><h2>Running</h2><p class="hint">${rec.length} repeating mission${rec.length === 1 ? "" : "s"}; these make up the totals above.</p></div>`;
    html += rec.length ? `<div class="cards">${rec.map(missionCard).join("")}</div>` : '<div class="empty">No repeating missions running.</div>';
    if (once.length) {
      html += `<div class="section-title"><h2>Not repeating</h2><p class="hint">Builds and missions with repeat off: comrades are busy, but they are left out of the per-hour totals.</p></div>`;
      html += `<div class="cards">${once.map(missionCard).join("")}</div>`;
    }
    if (view.portfolioUnknowns.length) html += `<p class="hint">Not counted: ${view.portfolioUnknowns.map(esc).join("; ")}</p>`;
    $("view-missions").innerHTML = html;
  }

  // ---------- available ----------
  const roleOf = (m) => (KEYS.includes((m.role || "").toLowerCase()) ? m.role.toLowerCase() : ROLE_OTHER);
  const bestNet = (m) => {
    if (!m.minNetPerHour) return -Infinity;
    const r = roleOf(m);
    if (ui.role !== "all" && ui.role !== ROLE_OTHER) return m.minNetPerHour[ui.role];
    if (r !== ROLE_OTHER) return m.minNetPerHour[r];
    return Math.max(...KEYS.map((k) => m.minNetPerHour[k]));
  };
  const isWanted = (a) => /^wanted level/.test(a);
  const otherAssumptions = (m) => (m.assumptions || []).filter((a) => !isWanted(a));
  function renderAvailableControls() {
    const regions = [...new Set(view.available.map((m) => m.region || "Other"))].sort();
    const sel = $("region"), cur = sel.value;
    sel.innerHTML = '<option value="">All regions</option>' + regions.map((r) => `<option${r === cur ? " selected" : ""}>${esc(r)}</option>`).join("");
    const roles = ["all", ...KEYS, ROLE_OTHER];
    $("roleChips").innerHTML = roles.map((r) => `<button class="chip" data-role="${r}" aria-pressed="${ui.role === r}">${r === "all" ? "All" : r === ROLE_OTHER ? "Other" : icon(r, 14) + LABEL[r]}</button>`).join("");
    $("fits").checked = ui.fits;
    $("sort").value = ui.sort;
  }
  function renderAvailable() {
    $("availCount").textContent = view.available.length;
    const q = ui.q.trim().toLowerCase();
    const free = view.workers.free;
    let list = view.available.filter((m) =>
      (!q || [m.name, m.type, m.location, m.category].some((s) => (s || "").toLowerCase().includes(q))) &&
      (!ui.region || (m.region || "Other") === ui.region) &&
      (ui.role === "all" || roleOf(m) === ui.role) &&
      (!ui.fits || free == null || (m.workersMin ?? 99) <= free));
    const by = {
      net: (a, b) => bestNet(b) - bestNet(a),
      dur: (a, b) => (a.durationHours ?? 1e9) - (b.durationHours ?? 1e9),
      workers: (a, b) => (a.workersMin ?? 99) - (b.workersMin ?? 99),
      name: (a, b) => (a.name || "").localeCompare(b.name || ""),
    }[ui.sort];
    list = list.slice().sort(by);
    if (!list.length) { $("availList").innerHTML = '<div class="empty">No missions match.</div>'; return; }
    const wanted = list.some((m) => (m.assumptions || []).some(isWanted));
    const note = `<p class="hint">Reward and net include the region's conversion bonus only; comrade skills and extra comrades add more.${wanted ? " Assumes your wanted level is below 4 (the save does not record it)." : ""}</p>`;
    const head = `<div class="row head" aria-hidden="true"><span>Mission</span><span>Comrades</span><span>Hours</span><span>Cost</span><span>Reward (at least)</span><span>Net (at least)</span><span></span><span></span></div>`;
    $("availList").innerHTML = note + head + list.map((m) => {
      const id = String(m.operationKey), open = ui.open.has(id);
      const flagged = m.unconfirmed && m.unconfirmed.length;
      const workers = m.workersMin === m.workersMax ? `${m.workersMin ?? "?"}` : `${m.workersMin}–${m.workersMax}`;
      const details = open ? `<div class="details">
          ${m.conversion != null ? `<div><span class="k">Bonus</span>+${m.conversion}% conversion included; ${esc(m.skillType ?? "skill")} skills and extra comrades add more.</div>` : ""}
          ${m.itemCost ? `<div><span class="k">Also costs</span>${esc(m.itemCost)}</div>` : ""}
          ${m.personalStorageReward ? `<div><span class="k">Storage</span>${esc(m.personalStorageReward)}</div>` : ""}
          ${m.category ? `<div><span class="k">Kind</span>${esc(m.category)}</div>` : ""}
          ${flagged ? `<div class="warn"><span class="k">Unconfirmed</span>${m.unconfirmed.map(esc).join("; ")}</div>` : ""}
          ${otherAssumptions(m).length ? `<div><span class="k">Assumes</span>${otherAssumptions(m).map(esc).join("; ")}</div>` : ""}
        </div>` : "";
      return `<div class="row" role="button" tabindex="0" data-op="${esc(id)}" aria-expanded="${open}">
        <span class="nm"><b>${esc(m.location ?? m.type ?? m.name)}</b><span>${esc(m.location ? m.type : m.region ?? "")}</span></span>
        <span class="num c-w">${workers}</span>
        <span class="num c-d">${m.durationHours ?? "?"}</span>
        <span class="num c-cost">${amounts(m.cost)}</span>
        <span class="num c-rew">${rewardCell(m, m.minReward)}</span>
        <span class="num c-net">${netList(m.minNetPerHour)}</span>
        <span class="c-add"><button class="btn small${inPlan(id) ? " on" : ""}" data-plan-add="${esc(id)}">${inPlan(id) ? "✓ In plan" : "+ Plan"}</button></span>
        <span class="c-flag">${flagged ? '<span class="flag" title="Some start conditions could not be confirmed">⚠</span>' : ""}</span>
        <span class="c-meta"><span>${workers} comrades</span><span>${m.durationHours ?? "?"} h</span>${amounts(m.cost)}</span>
        ${details}
      </div>`;
    }).join("");
  }

  // ---------- comrades ----------
  function renderComradeControls() {
    const types = [...new Set(view.comrades.flatMap((c) => (c.slots || []).filter(Boolean).map((s) => s.type)))].sort((a, b) => skillName(a).localeCompare(skillName(b)));
    const sel = $("cskill"), cur = ui.cskill;
    sel.innerHTML = '<option value="">Any skill</option>' + types.map((t) => `<option value="${esc(t)}"${t === cur ? " selected" : ""}>${esc(skillName(t))}</option>`).join("");
    const free = view.comrades.filter((c) => !c.busy).length;
    const kept = view.comrades.filter((c) => keptFor(c.key)).length;
    $("cstate").innerHTML = [["all", `All ${view.comrades.length}`], ["free", `Free ${free}`], ["busy", `On missions ${view.comrades.length - free}`], ["kept", `Kept on hand ${kept}`]]
      .map(([k, l]) => `<button class="chip" data-cstate="${k}" aria-pressed="${ui.cstate === k}">${l}</button>`).join("");
  }
  function slotRow(s, i, hit) {
    if (!s) return `<div class="slot none"><span class="n">${i + 1}</span>no camp skill</div>`;
    const gate = s.percent === 0;
    return `<div class="slot${s.unlocked ? "" : " locked"}${gate ? " gate" : ""}${hit === s.type ? " hit" : ""}"
        title="${s.unlocked ? "Unlocked" : "Unlocks at camp level " + (i + 1)}">
      <span class="n">${i + 1}</span>${esc(skillName(s.type))} ${ROMAN[s.tier] || s.tier}
      <span class="pct">${gate ? "allows missions" : "+" + s.percent + "%"}</span></div>`;
  }
  function renderComrades() {
    const q = ui.cq.trim().toLowerCase();
    let list = view.comrades.filter((c) =>
      (!q || c.name.toLowerCase().includes(q) || (c.missionName || "").toLowerCase().includes(q)) &&
      (ui.cstate === "all" || (ui.cstate === "kept" ? !!keptFor(c.key) : (ui.cstate === "free") === !c.busy)) &&
      (!ui.cskill || (c.slots || []).some((s) => s && s.type === ui.cskill)));
    if (ui.cskill) list = list.slice().sort((a, b) => matchOf(b, ui.cskill) - matchOf(a, ui.cskill) || a.name.localeCompare(b.name));
    else list = list.slice().sort((a, b) => (a.busy - b.busy) || a.name.localeCompare(b.name));
    if (!view.comrades.length) { $("comradeList").innerHTML = '<div class="empty">No comrades read from the save yet.</div>'; return; }
    if (!list.length) { $("comradeList").innerHTML = '<div class="empty">No comrades match.</div>'; return; }
    $("comradeList").innerHTML = list.map((c) => {
      const kept = keptFor(c.key), acts = activitiesOf(c);
      const keepCtl = c.key == null ? "" : kept
        ? `<div class="keep on"><span>Kept on hand for</span>
            <select data-keep-act="${esc(c.key)}" aria-label="Activity ${esc(c.name)} is kept for">
              ${[...new Set([...acts, kept, "General"])].map((t) => `<option value="${esc(t)}"${t === kept ? " selected" : ""}>${esc(skillName(t))}</option>`).join("")}
            </select>
            <button class="btn ghost small" data-keep-off="${esc(c.key)}">Release</button></div>`
        : `<div class="keep"><button class="btn ghost small" data-keep-on="${esc(c.key)}"
            title="Keep this comrade at camp for an activity: auto-pick and Optimize will leave them out">Keep on hand</button></div>`;
      return `<article class="card comrade${kept ? " kept" : ""}">
      <div class="card-head"><div><h3>${esc(c.name)}</h3>
        <div class="state ${c.busy ? "busy" : "free"}">${c.busy ? "On " + esc(c.missionName || "a mission") : "Free"}${kept ? ` · <span class="kept-tag">kept for ${esc(skillName(kept))}</span>` : ""}</div></div>
        ${ui.cskill ? `<span class="region" title="Unlocked ${esc(skillName(ui.cskill))} total">+${matchOf(c, ui.cskill)}%</span>` : ""}</div>
      <div class="slots">${c.slots ? c.slots.map((s, i) => slotRow(s, i, ui.cskill)).join("") : '<span class="unk">skills not read</span>'}</div>
      ${keepCtl}
    </article>`;
    }).join("");
  }
  function onComradeClick(e) {
    const t = e.target.closest("button"); if (!t) return;
    const d = t.dataset;
    if (d.keepOn) { const c = comradeByKey(d.keepOn); ui.keep[d.keepOn] = (c && activitiesOf(c)[0]) || "General"; }
    else if (d.keepOff) delete ui.keep[d.keepOff];
    else return;
    saveKeep(); renderComradeControls(); renderComrades(); if (ui.tab === "plan") renderPlan();
  }

  // ---------- plan (what-if) ----------
  function togglePlanAdd(op) {
    ui.simAdd = inPlan(op) ? ui.simAdd.filter((a) => String(a.op) !== String(op)) : [...ui.simAdd, { op: String(op), comrades: [] }];
    saveSim(); renderAvailable(); renderPlanBar();
  }
  function queueSim(delay = 150) {
    clearTimeout(simTimer);
    if (!planSize()) { sim = null; renderPlan(); renderPlanBar(); return; }
    simTimer = setTimeout(runSim, delay);
  }
  async function runSim() {
    const qs = `add=${encodeURIComponent(encodeCrews(ui.simAdd))}`
      + `&stop=${encodeURIComponent(ui.simStop.join(","))}&swap=${encodeURIComponent(encodeCrews(ui.simSwap))}`;
    const seq = ++simSeq;
    let result = null;
    try { result = await (await fetch(`/api/simulate?${qs}`, { cache: "no-store" })).json(); }
    catch { result = null; }
    if (seq !== simSeq) return;   // a newer plan was sent meanwhile
    sim = result;
    renderPlan(); renderPlanBar();
  }
  const availByOp = (op) => view && view.available.find((m) => String(m.operationKey) === String(op));
  const runningByOp = (op) => view && view.running.find((m) => String(m.operationKey) === String(op));
  const comradeByKey = (k) => view && view.comrades.find((c) => String(c.key) === String(k));
  const crewOf = (op) => view.comrades.filter((c) => c.key != null && String(c.operationKey) === String(op)).map((c) => String(c.key));
  // Comrades who could join this planned entry: not picked elsewhere in the plan, and free, freed by the plan, or
  // already on this mission. Kept-on-hand comrades come last and are never auto-picked.
  function candidates(entry, skillType) {
    const taken = new Set([...ui.simAdd, ...ui.simSwap].filter((x) => x !== entry).flatMap((x) => x.comrades.map(String)));
    const mine = new Set(entry.comrades.map(String));
    return view.comrades
      .filter((c) => c.key != null && !taken.has(String(c.key)) && !mine.has(String(c.key))
        && (!c.busy || stopping(c.operationKey) || switching(c.operationKey) || String(c.operationKey) === String(entry.op)))
      .map((c) => ({ c, match: matchOf(c, skillType), kept: keptFor(c.key) }))
      .sort((a, b) => !!a.kept - !!b.kept || b.match - a.match || a.c.name.localeCompare(b.c.name));
  }
  function toggleSwap(op) {
    ui.simSwap = switching(op) ? ui.simSwap.filter((s) => String(s.op) !== String(op)) : [...ui.simSwap, { op: String(op), comrades: crewOf(op) }];
    saveSim(); renderMissions(); renderPlanBar();
  }
  async function optimize() {
    const qs = `add=${encodeURIComponent(encodeCrews(ui.simAdd))}&stop=${encodeURIComponent(ui.simStop.join(","))}`
      + `&swap=${encodeURIComponent(encodeCrews(ui.simSwap))}&keep=${encodeURIComponent(Object.keys(ui.keep).join("."))}`
      + `&running=${ui.optRunning ? 1 : 0}`;
    let r;
    try { r = await (await fetch(`/api/optimize?${qs}`, { cache: "no-store" })).json(); }
    catch { optNotes = ["Optimize unavailable: the reader is not reachable."]; renderPlan(); return; }
    optUndo = { add: structuredClone(ui.simAdd), swap: structuredClone(ui.simSwap) };
    const norm = (l) => l.map((x) => ({ op: String(x.operationKey), comrades: x.comrades.map(String) }));
    const before = JSON.stringify([ui.simAdd, ui.simSwap]);
    ui.simAdd = norm(r.add); ui.simSwap = norm(r.swap);
    const changed = before !== JSON.stringify([ui.simAdd, ui.simSwap]);
    const n = ui.simSwap.length, said = [];
    if (ui.simAdd.length) said.push(`Picked crews for ${ui.simAdd.length} planned mission${ui.simAdd.length === 1 ? "" : "s"}.`);
    if (n) said.push(`Suggests switching the crew on ${n} running mission${n === 1 ? "" : "s"}.`);
    else if (ui.optRunning) said.push("Your running crews already have the best leads.");
    optNotes = [...said, ...r.notes];
    if (!changed) optUndo = null;
    saveSim(); renderMissions(); renderAvailable(); renderPlan(); renderPlanBar();
  }
  function delta(before, after, unit) {
    if (before == null || after == null) return '<span class="unk">unknown</span>';
    return rate(after - before, unit);
  }
  function renderPlanBar() {
    const bar = $("planBar"), n = planSize();
    $("planCount").hidden = n === 0;
    $("planCount").textContent = n;
    if (!n || ui.tab === "plan") { bar.hidden = true; return; }
    bar.hidden = false;
    const parts = sim ? sim.reserves.filter((r) => r.perHourBefore != null && r.perHourAfter != null && Math.abs(r.perHourAfter - r.perHourBefore) > 0.05)
      .map((r) => `<span class="amt">${icon(r.key, 14)}${delta(r.perHourBefore, r.perHourAfter)}</span>`) : [];
    bar.innerHTML = `<span class="t">Plan</span>
      <span class="d">${ui.simAdd.length ? `<span>+${ui.simAdd.length} mission${ui.simAdd.length === 1 ? "" : "s"}</span>` : ""}
        ${ui.simStop.length ? `<span>−${ui.simStop.length} stopped</span>` : ""}
        ${ui.simSwap.length ? `<span>⇄${ui.simSwap.length} switched</span>` : ""}
        ${parts.join("")}
        ${sim && sim.freeAfter != null ? `<span>${sim.freeAfter} comrades free</span>` : ""}</span>
      <button class="btn" data-open-plan>Review plan</button>`;
  }
  function optimizePanel() {
    return `<div class="optimize">
      <div class="opt-row"><button class="btn" data-optimize>Optimize comrades</button>
        <label class="check"><input type="checkbox" data-opt-running${ui.optRunning ? " checked" : ""}> Also switch crews on running missions</label>
        ${optUndo ? '<button class="btn ghost small" data-opt-undo>Undo</button>' : ""}</div>
      <p class="hint">Puts each mission's best skilled comrade in the lead (only the best one's skill counts) and fills the rest with
        comrades whose skills are worth least, so strong ones stay free. Crew sizes are not changed.
        ${Object.keys(ui.keep).length ? `Leaves out ${Object.keys(ui.keep).length} comrade${Object.keys(ui.keep).length === 1 ? "" : "s"} kept on hand.` : "Mark comrades <b>Keep on hand</b> under <b>Comrades</b> to leave them out."}
        Missions with bigger rewards per hour win ties; one unit of any reserve counts the same.</p>
      ${optNotes && optNotes.length ? `<ul class="opt-notes">${optNotes.map((n) => `<li>${esc(n)}</li>`).join("")}</ul>` : ""}
    </div>`;
  }
  // One planned entry: an added mission (kind "add") or a running one with a new crew (kind "swap").
  function crewCard(kind, a, m, r) {
    const need = m.workersMin ?? 0, max = m.workersMax ?? Math.max(need, a.comrades.length);
    const picked = a.comrades.map(comradeByKey).filter(Boolean);
    const cands = candidates(a, m.skillType ?? (m.bonus && m.bonus.skillType));
    const type = m.skillType ?? (m.bonus && m.bonus.skillType);
    const auto = cands.filter((x) => !x.kept);
    const b = r && r.bonus, id = `${kind}|${esc(a.op)}`;
    const now = kind === "swap" ? crewOf(a.op) : [];
    const pickChip = (c) => {
      const kept = keptFor(c.key), isNew = kind === "swap" && !now.includes(String(c.key));
      return `<span class="pick${kept ? " kept" : ""}${isNew ? " new" : ""}" title="${kept ? "Kept on hand for " + esc(skillName(kept)) : isNew ? "Joins this mission" : ""}">${esc(c.name)}
        <b class="pct">${type ? "+" + matchOf(c, type) + "%" : ""}</b>${kept ? '<span class="z">kept</span>' : ""}<button data-unpick="${id}|${esc(c.key)}" aria-label="Remove ${esc(c.name)}">×</button></span>`;
    };
    const leaving = kind === "swap" ? now.filter((k) => !a.comrades.map(String).includes(k)).map(comradeByKey).filter(Boolean) : [];
    return `<div class="planned${kind === "swap" ? " swap" : ""}">
      <div class="card-head"><div><h3>${esc(m.location ?? m.name)}</h3><div class="type">${kind === "swap" ? "Switch crew · " : ""}${esc(m.type ?? "")}</div></div>
        <button class="btn ghost small" data-${kind === "swap" ? "unswap" : "unplan"}="${esc(a.op)}">${kind === "swap" ? "Keep crew" : "Remove"}</button></div>
      <div class="flow">
        <span class="lbl">Comrades</span><span>${picked.length} picked · needs ${need}${max > need ? `–${max}` : ""}${type ? ` · uses <b>${esc(skillName(type))}</b>` : ""}</span>
        ${kind === "add" ? `<span class="lbl">Cost</span>${amounts(m.cost)}` : ""}
        <span class="lbl">Reward</span>${r ? amounts(r.reward) : '<span class="unk">…</span>'}${kind === "swap" && m.reward ? ` <span class="hint">now ${amounts(m.reward)}</span>` : ""}
        <span class="lbl">Net</span>${r ? netList(r.netPerHour) : '<span class="unk">…</span>'}
      </div>
      ${b ? `<div class="bonus">Bonus <b>${b.total == null ? "unknown" : "+" + b.total + "%"}</b> (${b.conversion ?? "?"}% conversion · ${b.skill ?? "?"}% skill · ${b.comrade ?? "?"}% comrades)${kind === "swap" && m.bonus && m.bonus.total != null ? ` · now +${m.bonus.total}%` : ""}</div>` : ""}
      <div class="crew">${picked.map(pickChip).join("")}
        ${picked.length < max && auto.length ? `<button class="btn small" data-autopick="${id}">${picked.length < need ? "Auto-pick best" : "Add best"}</button>` : ""}</div>
      ${leaving.length ? `<p class="hint">Comes off: ${leaving.map((c) => esc(c.name)).join(", ")}</p>` : ""}
      ${picked.length < max ? `<details${picked.length < need ? " open" : ""}><summary class="hint">Choose comrades (${cands.length} available${type ? ", best " + esc(skillName(type)) + " first" : ""})</summary>
        <div class="picker">${cands.map(({ c, match, kept }) => `<button class="cand${kept ? " kept" : ""}" data-pick="${id}|${esc(c.key)}">${esc(c.name)} ${type ? (match ? `<b>+${match}%</b>` : '<span class="z">+0%</span>') : ""}${kept ? ` <span class="z">(kept for ${esc(skillName(kept))})</span>` : c.busy && String(c.operationKey) !== String(a.op) ? ' <span class="z">(freed)</span>' : ""}</button>`).join("") || '<span class="hint">Nobody available.</span>'}</div></details>` : ""}
      ${need && max > need ? `<p class="hint">Each comrade above ${need} adds +20% (up to +40%).</p>` : ""}
      ${r && r.problems.length ? `<ul class="problems">${r.problems.map((p) => `<li>${esc(p)}</li>`).join("")}</ul>` : ""}
    </div>`;
  }
  function renderPlan() {
    const el = $("view-plan");
    if (!view) return;
    if (!planSize()) {
      el.innerHTML = `<div class="empty"><h2>No plan yet</h2><p class="hint">Use <b>+ Plan</b> on missions in <b>Available</b>, or <b>Switch crew</b> / <b>Stop in plan</b> on running ones under <b>Missions</b>.
        Then pick comrades here and see how your reserves and free comrades change before you commit in game.</p></div>
        <div class="panel" style="margin-top:14px">${optimizePanel()}</div>`;
      return;
    }
    if (!sim) { el.innerHTML = '<div class="empty">Working it out…</div>'; return; }
    const rows = sim.reserves.map((r) => `<tr><td><span class="amt">${icon(r.key, 14)}${LABEL[r.key]}</span></td>
        <td>${rate(r.perHourBefore, "")}</td><td>${rate(r.perHourAfter, "")}</td><td class="chg">${delta(r.perHourBefore, r.perHourAfter, "")}</td>
        <td>${rate(r.perDayAfter, "")}</td>
        <td>${r.hoursUntilEmptyAfter != null ? `<span class="neg">${hours(r.hoursUntilEmptyAfter)}</span>` : r.perHourAfter == null ? '<span class="unk">?</span>' : '<span class="ok">never</span>'}</td></tr>`).join("");
    const freeCls = sim.freeAfter != null && sim.freeAfter < 0 ? "neg" : "";
    const problems = [...sim.problems, ...(sim.operatingBudgetOkAfter === false ? ["food or funds would shrink every cycle (operating budget)"] : [])];
    const effect = `<div class="panel">
      ${optimizePanel()}
      <h2>Effect</h2>
      <div class="stat-line"><span>Comrades free</span><span class="big">${sim.freeBefore ?? "?"} → <span class="${freeCls}">${sim.freeAfter ?? "?"}</span></span>
        <span class="hint">of ${sim.total ?? "?"}</span></div>
      <div class="table-wrap"><table class="effect"><thead><tr><th>Reserve</th><th>Now /h</th><th>Plan /h</th><th>Change</th><th>Plan /game day</th><th>Runs out</th></tr></thead>
        <tbody>${rows}</tbody></table></div>
      ${problems.length ? `<ul class="problems">${problems.map((p) => `<li>${esc(p)}</li>`).join("")}</ul>` : ""}
      ${ui.simStop.length ? `<h3 style="margin-top:14px">Stopping</h3><div class="crew">${ui.simStop.map((op) => {
          const r = runningByOp(op);
          return `<span class="pick">${esc(r ? (r.location ?? r.name) + (r.type ? " · " + r.type : "") : "mission " + op)}<button data-unstop="${esc(op)}" aria-label="Keep running">×</button></span>`;
        }).join("")}</div>` : ""}
      <p class="hint">Per-day figures are per in-game day (24 in-game hours, about ${realDay()} real). Totals count repeating missions only.
        Switching a crew in game may restart that mission's timer; the plan does not count lost progress.</p>
      <div style="margin-top:10px"><button class="btn ghost" data-clear-plan>Clear plan</button></div>
    </div>`;
    const swaps = ui.simSwap.map((a) => {
      const m = runningByOp(a.op), r = (sim.switched || []).find((x) => String(x.operationKey) === String(a.op));
      if (!m) return `<div class="planned"><b>Mission ${esc(a.op)}</b><span class="neg">No longer running.</span><div><button class="btn ghost small" data-unswap="${esc(a.op)}">Remove</button></div></div>`;
      return crewCard("swap", a, m, r);
    }).join("");
    const adds = ui.simAdd.map((a) => {
      const m = availByOp(a.op), r = sim.added.find((x) => String(x.operationKey) === String(a.op));
      if (!m) return `<div class="planned"><b>Mission ${esc(a.op)}</b><span class="neg">No longer available.</span><div><button class="btn ghost small" data-unplan="${esc(a.op)}">Remove</button></div></div>`;
      return crewCard("add", a, m, r);
    }).join("");
    const cards = (swaps ? `<h3>Switching crews</h3>${swaps}` : "") + (adds ? `${swaps ? "<h3>New missions</h3>" : ""}${adds}` : "");
    el.innerHTML = `<div class="plan-grid">${effect}<div class="cards" style="grid-template-columns:1fr;margin:0">${cards || '<div class="empty">No missions added. Add some from <b>Available</b>.</div>'}</div></div>`;
  }
  function onPlanClick(e) {
    const t = e.target.closest("button, input[data-opt-running]"); if (!t) return;
    const d = t.dataset;
    const find = (kind, op) => (kind === "swap" ? ui.simSwap : ui.simAdd).find((a) => String(a.op) === String(op));
    const maxOf = (kind, op, a) => {
      const m = kind === "swap" ? runningByOp(op) : availByOp(op);
      return m ? [m.workersMin ?? 0, m.workersMax ?? Math.max(m.workersMin ?? 0, a.comrades.length), m.skillType ?? (m.bonus && m.bonus.skillType)] : null;
    };
    if (d.optRunning !== undefined) { ui.optRunning = t.checked; store.set("optRunning", ui.optRunning); return; }
    if (d.optimize !== undefined) { optimize(); return; }
    if (d.optUndo !== undefined) {
      if (!optUndo) return;
      ui.simAdd = optUndo.add; ui.simSwap = optUndo.swap; optUndo = null; optNotes = null;
      saveSim(); renderMissions(); renderAvailable(); renderPlan(); renderPlanBar(); return;
    }
    optNotes = null;
    if (d.unplan) { ui.simAdd = ui.simAdd.filter((a) => String(a.op) !== d.unplan); renderAvailable(); }
    else if (d.unswap) { ui.simSwap = ui.simSwap.filter((a) => String(a.op) !== d.unswap); renderMissions(); }
    else if (d.unstop) { ui.simStop = ui.simStop.filter((x) => String(x) !== d.unstop); renderMissions(); }
    else if (d.clearPlan !== undefined) { ui.simAdd = []; ui.simStop = []; ui.simSwap = []; optUndo = null; renderAvailable(); renderMissions(); }
    else if (d.pick) { const [kind, op, k] = d.pick.split("|"); const a = find(kind, op); if (a && !a.comrades.includes(k)) a.comrades.push(k); }
    else if (d.unpick) { const [kind, op, k] = d.unpick.split("|"); const a = find(kind, op); if (a) a.comrades = a.comrades.filter((x) => String(x) !== k); }
    else if (d.autopick) {
      const [kind, op] = d.autopick.split("|"), a = find(kind, op); if (!a) return;
      const lim = maxOf(kind, op, a); if (!lim) return;
      const [need, max, type] = lim;
      const target = Math.min(a.comrades.length < need ? need : a.comrades.length + 1, max);
      for (const { c, kept } of candidates(a, type)) { if (a.comrades.length >= target) break; if (!kept) a.comrades.push(String(c.key)); }
    } else return;
    saveSim(); renderPlan(); renderPlanBar();
  }

  // ---------- planner ----------
  let planTimer = 0;
  function renderPlannerInputs() {
    $("planInputs").innerHTML = KEYS.map((k) => `<label><span>${icon(k, 14)}${LABEL[k]}</span>
      <input type="number" min="0" step="1" inputmode="numeric" data-plan="${k}" value="${ui.plan[k] || ""}" placeholder="0"></label>`).join("");
  }
  async function updatePlan() {
    const qs = KEYS.map((k) => `${k}=${encodeURIComponent(Math.max(0, Math.floor(+ui.plan[k] || 0)))}`).join("&");
    try {
      const rows = await (await fetch(`/api/plan?${qs}`, { cache: "no-store" })).json();
      const afford = (a) => a.kind === "Ready" ? '<span class="ok">Ready</span>'
        : a.kind === "Hours" ? `${hours(a.hours)} of play${realEta(a.hours)}`
        : a.kind === "NotAtCurrentNet" ? '<span class="neg">Not at current net</span>' : '<span class="unk">unknown</span>';
      const anyCost = KEYS.some((k) => +ui.plan[k] > 0);
      $("planOut").innerHTML = `<div class="table-wrap"><table>
        <thead><tr><th>Reserve</th><th>Have</th><th>Need</th><th>Short</th><th>Time to afford</th></tr></thead>
        <tbody>${rows.map((r) => `<tr><td><span class="amt">${icon(r.key, 14)}${LABEL[r.key]}</span></td>
          <td>${fmt(r.balance)}</td><td>${fmt(r.totalNeeded)}</td>
          <td>${r.shortfall ? `<span class="neg">${nf.format(r.shortfall)}</span>` : r.shortfall === 0 ? '<span class="ok">0</span>' : fmt(null)}</td>
          <td>${afford(r.afford)}</td></tr>`).join("")}</tbody></table></div>
        ${anyCost ? "" : '<p class="hint">With no upgrade cost entered, this shows whether you can afford one more cycle of your running missions.</p>'}`;
    } catch { $("planOut").innerHTML = '<p class="hint">Planner unavailable: the reader is not reachable.</p>'; }
  }

  // ---------- history ----------
  function renderHistChips() {
    $("histChips").innerHTML = KEYS.map((k) => `<button class="chip" data-hist="${k}" aria-pressed="${ui.hist === k}">${icon(k, 14)}${LABEL[k]}</button>`).join("");
  }
  function renderChart() {
    const pts = view.history.filter((p) => p[ui.hist] != null).map((p) => ({ t: new Date(p.t).getTime(), v: p[ui.hist], s: p.source }));
    const el = $("chart");
    if (pts.length < 2) { el.innerHTML = '<div class="empty">Not enough snapshots yet. Play a while and this fills in.</div>'; return; }
    const W = 900, H = 280, L = 64, R = 14, T = 14, B = 30;
    const t0 = pts[0].t, t1 = pts[pts.length - 1].t || t0 + 1;
    let lo = Math.min(...pts.map((p) => p.v)), hi = Math.max(...pts.map((p) => p.v));
    if (lo === hi) { lo -= 1; hi += 1; }
    const pad = (hi - lo) * 0.08; lo -= pad; hi += pad;
    const x = (t) => L + (W - L - R) * ((t - t0) / Math.max(1, t1 - t0));
    const y = (v) => T + (H - T - B) * (1 - (v - lo) / (hi - lo));
    const path = pts.map((p, i) => `${i ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join("");
    const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => lo + (hi - lo) * f);
    const tf = (t) => { const d = new Date(t); return (t1 - t0 > 86400000 ? d.toLocaleDateString(undefined, { month: "short", day: "numeric" }) + " " : "") + d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }); };
    const change = pts[pts.length - 1].v - pts[0].v;
    el.innerHTML = `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-label="${LABEL[ui.hist]} over time">
      <defs><linearGradient id="fillG" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#c9a45c" stop-opacity=".28"/><stop offset="1" stop-color="#c9a45c" stop-opacity="0"/></linearGradient></defs>
      ${ticks.map((v) => `<line class="grid" x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}"/><text x="${L - 8}" y="${y(v) + 4}" text-anchor="end">${nf.format(Math.round(v))}</text>`).join("")}
      <line class="axis" x1="${L}" x2="${W - R}" y1="${H - B}" y2="${H - B}"/>
      <text x="${L}" y="${H - 8}">${tf(t0)}</text><text x="${W - R}" y="${H - 8}" text-anchor="end">${tf(t1)}</text>
      <path class="area" d="${path}L${x(t1)},${H - B}L${x(t0)},${H - B}Z"/>
      <path class="lineP" d="${path}" vector-effect="non-scaling-stroke"/>
      ${pts.length <= 120 ? pts.map((p) => `<circle class="${p.s === "process" ? "pt-live" : "pt-save"}" cx="${x(p.t)}" cy="${y(p.v)}" r="2.6"><title>${nf.format(p.v)} · ${new Date(p.t).toLocaleString()} · ${p.s === "process" ? "memory" : "save"}</title></circle>`).join("") : ""}
    </svg>
    <p class="hint">${LABEL[ui.hist]}: ${nf.format(pts[pts.length - 1].v)} now, <span class="${change >= 0 ? "pos" : "neg"}">${change >= 0 ? "+" : ""}${nf.format(change)}</span> over ${pts.length} snapshots.</p>`;
  }

  function renderUnknowns() {
    const list = view.unknowns.filter((u) => !/^mission rewards are base amounts/.test(u));
    const d = $("unknowns");
    d.hidden = list.length === 0;
    d.querySelector("summary").textContent = `What couldn't be read (${list.length})`;
    d.querySelector("ul").innerHTML = list.map((u) => `<li>${esc(u)}</li>`).join("");
  }

  // ---------- tabs ----------
  function showTab(name, focus) {
    if (!TABS.includes(name)) name = "missions";
    ui.tab = name; store.set("tab", name);
    for (const t of TABS) {
      const on = t === name;
      $("tab-" + t).setAttribute("aria-selected", on);
      $("tab-" + t).tabIndex = on ? 0 : -1;
      $("view-" + t).hidden = !on;
    }
    if (focus) $("tab-" + name).focus();
    if (name === "planner") updatePlan();
    if (name === "history" && view) renderChart();
    if (name === "plan") queueSim(0);
  }

  function render() {
    if (!view) return;
    renderHeader();
    renderReserves();
    renderMissions();
    renderAvailableControls();
    renderAvailable();
    renderComradeControls();
    renderComrades();
    if (planSize()) queueSim(); else renderPlan();
    if (ui.tab === "history") renderChart();
    if (ui.tab === "planner") updatePlan();
    renderUnknowns();
  }

  // ---------- events ----------
  function wire() {
    const tabs = TABS;
    tabs.forEach((t) => $("tab-" + t).addEventListener("click", () => showTab(t)));
    document.querySelector(".tabs").addEventListener("keydown", (e) => {
      const i = tabs.indexOf(ui.tab);
      if (e.key === "ArrowRight") showTab(tabs[(i + 1) % tabs.length], true);
      if (e.key === "ArrowLeft") showTab(tabs[(i + tabs.length - 1) % tabs.length], true);
    });
    document.addEventListener("keydown", (e) => {
      if (e.target.matches("input, select, textarea") || e.ctrlKey || e.metaKey || e.altKey) return;
      if (/^[1-6]$/.test(e.key)) showTab(tabs[+e.key - 1]);
      if (e.key === "/") { e.preventDefault(); showTab("available"); $("q").focus(); }
    });
    $("q").addEventListener("input", (e) => { ui.q = e.target.value; renderAvailable(); });
    $("region").addEventListener("change", (e) => { ui.region = e.target.value; renderAvailable(); });
    $("fits").addEventListener("change", (e) => { ui.fits = e.target.checked; store.set("fits", ui.fits); renderAvailable(); });
    $("sort").addEventListener("change", (e) => { ui.sort = e.target.value; store.set("sort", ui.sort); renderAvailable(); });
    $("roleChips").addEventListener("click", (e) => {
      const b = e.target.closest("[data-role]"); if (!b) return;
      ui.role = b.dataset.role; store.set("role", ui.role); renderAvailableControls(); renderAvailable();
    });
    $("availList").addEventListener("click", (e) => {
      const add = e.target.closest("[data-plan-add]");
      if (add) { e.stopPropagation(); togglePlanAdd(add.dataset.planAdd); return; }
      const r = e.target.closest("[data-op]"); if (!r) return;
      const id = r.dataset.op; ui.open.has(id) ? ui.open.delete(id) : ui.open.add(id); renderAvailable();
    });
    $("availList").addEventListener("keydown", (e) => {
      if (e.key !== "Enter" && e.key !== " ") return;
      const r = e.target.closest("[data-op]"); if (!r || r !== e.target) return;
      e.preventDefault(); r.click();
      const again = document.querySelector(`[data-op="${CSS.escape(r.dataset.op)}"]`); if (again) again.focus();
    });
    $("view-missions").addEventListener("click", (e) => {
      const sw = e.target.closest("[data-plan-swap]");
      if (sw) { toggleSwap(sw.dataset.planSwap); return; }
      const b = e.target.closest("[data-plan-stop]"); if (!b) return;
      const op = b.dataset.planStop;
      ui.simStop = stopping(op) ? ui.simStop.filter((x) => String(x) !== op) : [...ui.simStop, op];
      if (stopping(op)) ui.simSwap = ui.simSwap.filter((s) => String(s.op) !== op);   // stopped wins over switched
      saveSim(); renderMissions(); renderPlanBar();
    });
    $("comradeList").addEventListener("click", onComradeClick);
    $("comradeList").addEventListener("change", (e) => {
      const k = e.target.dataset.keepAct; if (!k) return;
      ui.keep[k] = e.target.value; saveKeep(); renderComrades();
    });
    $("view-plan").addEventListener("click", onPlanClick);
    $("planBar").addEventListener("click", (e) => { if (e.target.closest("[data-open-plan]")) showTab("plan"); });
    $("cq").addEventListener("input", (e) => { ui.cq = e.target.value; renderComrades(); });
    $("cskill").addEventListener("change", (e) => { ui.cskill = e.target.value; renderComrades(); });
    $("cstate").addEventListener("click", (e) => {
      const b = e.target.closest("[data-cstate]"); if (!b) return;
      ui.cstate = b.dataset.cstate; store.set("cstate", ui.cstate); renderComradeControls(); renderComrades();
    });
    $("histChips").addEventListener("click", (e) => {
      const b = e.target.closest("[data-hist]"); if (!b) return;
      ui.hist = b.dataset.hist; store.set("hist", ui.hist); renderHistChips(); renderChart();
    });
    $("planInputs").addEventListener("input", (e) => {
      const k = e.target.dataset.plan; if (!k) return;
      ui.plan[k] = e.target.value; store.set("plan", ui.plan);
      clearTimeout(planTimer); planTimer = setTimeout(updatePlan, 200);
    });
  }

  wire();
  renderPlannerInputs();
  renderHistChips();
  showTab(ui.tab);
  poll();
  setInterval(poll, 3000);
  setInterval(() => { if (view) renderStatus(); }, 1000);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) poll(); });
})();
