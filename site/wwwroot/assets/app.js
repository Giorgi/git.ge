// git.ge progressive enhancement. Every page works without this file; it adds
// the English toggle, client-side sorting and search. No cookies: the language
// choice is kept in localStorage, and only if storage is available.
(() => {
  "use strict";

  const I18N = window.GITGE_I18N || { ka: {}, en: {} };
  const EN = I18N.en;
  const root = document.documentElement;
  // A UI string in the current language.
  const t = (key, ...a) => format((root.lang === "en" ? I18N.en : I18N.ka)[key] ?? key, a);

  // ---- Language -----------------------------------------------------------

  const format = (s, args) => s.replace(/\{(\d+)\}/g, (_, i) => args[i] ?? "");
  const args = (el, name) => JSON.parse(el.getAttribute(name) || "[]");
  // Text that app.js builds itself re-renders through these after a language switch.
  const languageHooks = [];

  function applyLanguage(lang) {
    root.lang = lang;
    const en = lang === "en";

    for (const el of document.querySelectorAll("[data-i18n]")) {
      const key = el.dataset.i18n;
      if (!key) continue;
      if (el.dataset.ka === undefined) el.dataset.ka = el.textContent;
      el.textContent = en && EN[key] !== undefined
        ? (el.dataset.i18nPrefix || "") + format(EN[key], args(el, "data-i18n-args")) + (el.dataset.i18nSuffix || "")
        : el.dataset.ka;
    }
    for (const [attr, data] of [["title", "i18nTitle"], ["placeholder", "i18nPlaceholder"]]) {
      for (const el of document.querySelectorAll(`[data-${attr === "title" ? "i18n-title" : "i18n-placeholder"}]`)) {
        const key = el.dataset[data];
        const saved = "ka" + attr;
        if (el.dataset[saved] === undefined) el.dataset[saved] = el.getAttribute(attr) || "";
        el.setAttribute(attr, en && EN[key] !== undefined
          ? format(EN[key], args(el, `data-i18n-${attr}-args`))
          : el.dataset[saved]);
      }
    }
    // Hand-written Georgian descriptions switch back to the original English one.
    for (const el of document.querySelectorAll("[data-desc-en]")) {
      if (el.dataset.ka === undefined) el.dataset.ka = el.textContent;
      el.textContent = en ? el.dataset.descEn : el.dataset.ka;
    }
    // Truncated lines carry their full text as a tooltip; keep it in the shown language.
    for (const el of document.querySelectorAll("[data-title-from-text]")) {
      el.title = el.textContent.replace(/\s+/g, " ").trim();
    }
    for (const hook of languageHooks) hook();
  }

  function storedLanguage() {
    try { return localStorage.getItem("lang"); } catch { return null; }
  }

  function storeLanguage(lang) {
    try { localStorage.setItem("lang", lang); } catch { /* private mode: fine */ }
  }

  const toggle = document.querySelector(".lang");
  if (toggle) {
    toggle.hidden = false;
    toggle.addEventListener("click", () => {
      const lang = root.lang === "en" ? "ka" : "en";
      applyLanguage(lang);
      storeLanguage(lang);
    });
  }
  // ?lang=en (or ka) wins over the stored choice, so an English link stays English.
  const requested = new URLSearchParams(location.search).get("lang");
  if (requested === "en" || requested === "ka") storeLanguage(requested);
  if ((requested || storedLanguage()) === "en") applyLanguage("en");

  // ---- Sorting --------------------------------------------------------------

  const sorters = {
    trend: el => Number(el.dataset.trend === "" ? -1e9 : el.dataset.trend),
    stars: el => Number(el.dataset.stars || 0),
    pushed: el => el.dataset.pushed || "",
    created: el => el.dataset.created || "",
    // Developer directory (/developers/)
    name: el => (el.querySelector("strong")?.textContent || "").toLowerCase(),
    projects: el => Number(el.dataset.projects || 0),
    followed: el => Number(el.dataset.followed || 0),
    first: el => el.dataset.first || "",
  };
  const ascending = new Set(["name"]);

  function sortList(list, key) {
    const value = sorters[key];
    const dir = ascending.has(key) ? -1 : 1;
    const items = [...list.children];
    items.sort((a, b) => {
      const x = value(a), y = value(b);
      if (dir === -1 && typeof x === "string") return x.localeCompare(y);
      return x < y ? dir : x > y ? -dir : Number(b.dataset.stars || 0) - Number(a.dataset.stars || 0);
    });
    list.append(...items);
  }

  for (const bar of document.querySelectorAll(".sort[data-sort-for]")) {
    const list = document.getElementById(bar.dataset.sortFor);
    if (!list) continue;
    bar.hidden = false;

    // Grouped lists (the /developers/ directory): the server renders a first list plus a second
    // group (data-groups) in their default order. Any other sort merges everyone into
    // the first list; choosing the group sort again restores the original groups.
    const extra = bar.dataset.groups ? document.getElementById(bar.dataset.groups) : null;
    const extraTitle = extra ? document.querySelector(`[data-group-title="${extra.id}"]`) : null;
    const original = extra ? { main: [...list.children], extra: [...extra.children] } : null;
    const metricHost = list.closest("[data-metric]");

    // Developer cards: the metric line for the active sort, built from data-* so the
    // HTML only carries the default meta line.
    const metrics = {
      followed: el => t("devs.metricFollowed", el.dataset.followed || "0"),
      pushed: el => t("devs.metricPushed", el.dataset.pushed || "—"),
      first: el => t("devs.metricFirst", el.dataset.first || "—"),
    };
    const showMetric = key => {
      if (!metricHost) return;
      metricHost.dataset.metric = key;
      const make = metrics[key];
      if (!make) return;
      for (const li of metricHost.querySelectorAll("li[data-cats]")) {
        let line = li.querySelector(".m-metric");
        if (!line) {
          line = document.createElement("span");
          line.className = "dev-card-meta m-metric";
          li.querySelector(".m-default").after(line);
        }
        line.textContent = make(li);
        line.title = line.textContent;
      }
    };
    if (metricHost) {
      // The default meta line is plain Georgian in the HTML; rebuild it (and its
      // tooltip) from data-projects and data-cats in the current language.
      const rebuildMeta = () => {
        for (const li of metricHost.querySelectorAll("li[data-cats]")) {
          const meta = li.querySelector(".m-default");
          const cats = li.dataset.cats ? li.dataset.cats.split(",") : [];
          meta.textContent = [t("devs.projects", li.dataset.projects), ...cats.map(c => t("cat." + c))].join(" · ");
          meta.title = meta.textContent;
        }
      };
      languageHooks.push(rebuildMeta, () => showMetric(metricHost.dataset.metric));
      rebuildMeta();   // also sets the tooltips
    }

    bar.addEventListener("click", e => {
      const button = e.target.closest("button[data-sort]");
      if (!button) return;
      const key = button.dataset.sort;
      for (const b of bar.querySelectorAll("button")) b.setAttribute("aria-pressed", String(b === button));
      bar.dataset.current = key;
      showMetric(key);
      if (original && key === bar.dataset.groupSort) {
        list.replaceChildren(...original.main);
        extra.replaceChildren(...original.extra);
        extra.hidden = false;
        if (extraTitle) extraTitle.hidden = false;
        return;
      }
      if (original) {
        list.append(...extra.children);
        extra.hidden = true;
        if (extraTitle) extraTitle.hidden = true;
      }
      sortList(list, key);
    });
  }

  // ---- Search (/projects/) --------------------------------------------------

  const search = document.querySelector(".search");
  if (!search) return;
  search.hidden = false;

  const input = search.querySelector("input");
  const status = document.getElementById("q-status");
  const list = document.getElementById("list");
  const pager = document.querySelector(".pager");
  const original = [...list.children];
  let index = null;
  let loading = null;

  const load = () => loading ??= fetch("/index.json").then(r => r.json()).then(data => {
    index = data;
    for (const p of data.projects) {
      p.text = [p.n, p.d, p.k, p.l, p.c, ...(p.g || [])].filter(Boolean).join(" ").toLowerCase();
    }
  });

  input.addEventListener("focus", load, { once: true });
  input.addEventListener("input", async () => {
    const query = input.value.trim().toLowerCase();
    if (!query) {
      list.replaceChildren(...original);
      if (pager) pager.hidden = false;
      status.textContent = "";
      return;
    }
    await load();
    if (input.value.trim().toLowerCase() !== query) return; // a newer keystroke won

    const words = query.split(/\s+/);
    const hits = index.projects.filter(p => words.every(w => p.text.includes(w)));
    list.replaceChildren(...hits.slice(0, 200).map(card));
    if (pager) pager.hidden = true;

    status.textContent = hits.length ? t("projects.results", hits.length) : t("projects.noResults");

    const current = document.querySelector(".sort[data-sort-for='list']")?.dataset.current;
    if (current) sortList(list, current);
  });

  // Mirrors ProjectCard.razor. textContent everywhere: index data is never parsed as HTML.
  function card(p) {
    const el = (tag, cls, text) => {
      const e = document.createElement(tag);
      if (cls) e.className = cls;
      if (text !== undefined) e.textContent = text;
      return e;
    };
    const li = el("li", "p");
    li.dataset.stars = p.s ?? 0;
    li.dataset.trend = p.t ?? "";
    li.dataset.pushed = p.p ?? "";
    li.dataset.created = p.r ?? "";

    const head = el("div", "p-head");
    const title = el("span", "p-title");
    const name = el("a", "p-name");
    name.href = p.u || "https://github.com/" + p.n;
    // "u" is only set for URLs from submissions (not GitHub): don't vouch for them.
    if (p.u) name.rel = "nofollow ugc";
    const slash = p.n.indexOf("/");
    if (slash > 0) {
      const ownerLogin = p.n.slice(0, slash);
      const owner = el("a", "p-owner", ownerLogin + "/");
      // "w": the owner has a developer page on git.ge.
      owner.href = p.w ? "/@" + ownerLogin.toLowerCase() + "/" : "https://github.com/" + ownerLogin;
      title.append(owner);
      name.textContent = p.n.slice(slash + 1);
    } else name.textContent = p.n;
    title.append(name);
    head.append(title);
    if (p.a) head.append(el("span", "badge", t("card.archived")));
    if (p.h) {
      const hw = el("a", "badge hw", t("card.helpWanted", p.h));
      hw.href = "/help-wanted/#" + p.x;
      head.append(hw);
    }
    li.append(head);

    const desc = root.lang === "en" ? p.d : (p.k || p.d);
    if (desc) li.append(el("p", "p-desc", desc));

    const meta = el("div", "p-meta");
    if (p.l) meta.append(el("span", "p-lang", p.l));
    if (p.s !== undefined) meta.append(el("span", "p-stars", "★ " + p.s.toLocaleString("en-US")));
    if (index.trend && p.s !== undefined) {
      const t = p.t;
      meta.append(el("span", "p-trend" + (t > 0 ? " up" : t < 0 ? " down" : ""),
        t === undefined ? "—" : t > 0 ? "+" + t : t < 0 ? "−" + -t : "0"));
    }
    if (p.p) {
      const time = el("time", "p-pushed", p.p);
      time.dateTime = p.p;
      meta.append(time);
    }
    li.append(meta);
    return li;
  }
})();
