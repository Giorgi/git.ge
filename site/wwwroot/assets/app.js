// git.ge progressive enhancement. Every page works without this file; it adds
// the English toggle, client-side sorting and search. No cookies: the language
// choice is kept in localStorage, and only if storage is available.
(() => {
  "use strict";

  const I18N = window.GITGE_I18N || { ka: {}, en: {} };
  const EN = I18N.en;
  const root = document.documentElement;

  // /developers/ sidebar on desktop: folded unless the visitor expanded it before.
  // Set first thing so the directory never renders in the other state (the column
  // itself only appears once app.js reveals it).
  const sidebarKey = "gitge.devSidebar";
  const storedSidebar = () => { try { return localStorage.getItem(sidebarKey); } catch { return null; } };
  root.classList.toggle("devs-collapsed", storedSidebar() !== "expanded");
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
          // data-cats is "shown,categories;other,categories": only the shown part goes on the card.
          const shown = (li.dataset.cats || "").split(";")[0];
          const cats = shown ? shown.split(",") : [];
          meta.textContent = [t("devs.projects", li.dataset.projects), ...cats.map(c => t("cat." + c))].join(" · ");
          meta.title = meta.textContent;
        }
      };
      languageHooks.push(rebuildMeta, () => showMetric(metricHost.dataset.metric));
      rebuildMeta();   // also sets the tooltips
    }

    // Sort by buttons (project lists) or by a <select data-sort-select> (the directory).
    const sortSelect = bar.querySelector("select[data-sort-select]");
    bar.addEventListener("click", e => {
      const button = e.target.closest("button[data-sort]");
      if (button) choose(button.dataset.sort);
    });
    sortSelect?.addEventListener("change", () => choose(sortSelect.value));

    function choose(key) {
      for (const b of bar.querySelectorAll("button[data-sort]")) b.setAttribute("aria-pressed", String(b.dataset.sort === key));
      bar.dataset.current = key;
      showMetric(key);
      if (original && key === bar.dataset.groupSort) {
        list.replaceChildren(...original.main);
        extra.replaceChildren(...original.extra);
        extra.hidden = false;
        if (extraTitle) extraTitle.hidden = false;
      } else {
        if (original) {
          list.append(...extra.children);
          extra.hidden = true;
          if (extraTitle) extraTitle.hidden = true;
        }
        sortList(list, key);
      }
      // The directory filters re-apply after a sort (group headings, hidden cards).
      bar.dispatchEvent(new CustomEvent("sorted"));
    }
  }

  // ---- Filters (/developers/) -------------------------------------------------
  // One category and one language at a time (AND across the two). Either alone
  // matches when ANY of a developer's projects has it (data-cats / data-langs); both
  // together need ONE project with both (data-pairs, or data-cats × data-langs when
  // the server left data-pairs out because they're equivalent). Each control is
  // either a row of chips (buttons) or a <select>; every option carries data-value
  // ("" = all). Counts are recounted against the other active filter; options with 0
  // are disabled. State lives in ?cat= & ?language= (not ?lang=, which switches the
  // interface language).

  const filters = document.querySelector(".dev-filters[data-filter-for]");
  if (filters) {
    const host = document.getElementById(filters.dataset.filterFor);
    const count = filters.querySelector(".dev-filter-count");
    const clear = filters.querySelector(".dev-filter-clear");
    const empty = document.querySelector(".dev-filter-empty");
    const active = { cat: null, lang: null };
    const param = { cat: "cat", lang: "language" };   // URL parameter names
    filters.hidden = false;

    // The two controls, whatever their form.
    const controls = {};
    for (const kind of ["cat", "lang"]) {
      const el = filters.querySelector(`[data-filter="${kind}"]`);
      if (!el) continue;
      controls[kind] = el.tagName === "SELECT"
        ? { kind, el, select: true, items: [...el.options] }
        : { kind, el, select: false, items: [...el.querySelectorAll("button[data-value]")] };
    }
    const label = (kind, value) => kind === "cat" ? t("cat." + value) : value;

    // Parse each card once. data-pairs: "mobile:Kotlin,Swift|web:C#,TypeScript".
    const split = s => (s ? s.split(",") : []);
    const cards = [...host.querySelectorAll("li[data-cats]")].map(li => ({
      li,
      cats: new Set(split((li.dataset.cats || "").replace(";", ","))),
      langs: new Set(split(li.dataset.langs)),
      pairs: li.dataset.pairs
        ? new Map(li.dataset.pairs.split("|").map(e => [e.slice(0, e.indexOf(":")), new Set(split(e.slice(e.indexOf(":") + 1)))]))
        : null,
    }));
    const matches = (card, cat, lang) => {
      if (cat && !card.cats.has(cat)) return false;
      if (lang && !card.langs.has(lang)) return false;
      if (cat && lang && card.pairs) return card.pairs.get(cat)?.has(lang) === true;
      return true;
    };
    const countFor = (cat, lang) => cards.reduce((sum, card) => sum + (matches(card, cat, lang) ? 1 : 0), 0);

    const apply = () => {
      let shown = 0;
      for (const card of cards) {
        const match = matches(card, active.cat, active.lang);
        card.li.hidden = !match;
        if (match) shown++;
      }
      // In the grouped view, hide a group heading whose list has no visible cards.
      for (const title of host.querySelectorAll("[data-group-title]")) {
        const list = document.getElementById(title.dataset.groupTitle);
        if (!list || list.hidden) continue;   // merged by a sort: the sort code hides it
        title.hidden = ![...list.children].some(li => !li.hidden);
      }
      // Each option counts the developers it would show together with the other filter.
      for (const control of Object.values(controls)) {
        const { kind, select } = control;
        const other = kind === "cat" ? "lang" : "cat";
        for (const item of control.items) {
          const value = item.dataset.value || null;
          const n = countFor(kind === "cat" ? value : active.cat, kind === "lang" ? value : active.lang);
          const on = value === active[kind];
          const inert = n === 0 && !on && value !== null && Boolean(active[other]);
          if (select) {
            item.textContent = value === null ? t("devs.filterAll") : `${label(kind, value)} (${n})`;
            item.disabled = inert;
          } else {
            item.querySelector(".n").textContent = n;
            item.setAttribute("aria-pressed", String(on));
            if (inert) item.setAttribute("aria-disabled", "true"); else item.removeAttribute("aria-disabled");
          }
        }
        if (select) control.el.value = active[kind] || "";
      }
      const filtering = Boolean(active.cat || active.lang);
      const showCount = filtering || "countAlways" in filters.dataset;
      count.textContent = showCount ? (shown === 1 ? t("devs.filterCountOne") : t("devs.filterCount", shown)) : "";
      clear.hidden = !filtering;
      empty.hidden = shown > 0;
      for (const hook of afterApply) hook();
    };
    const afterApply = [];

    const saveUrl = () => {
      const params = new URLSearchParams(location.search);
      for (const kind of ["cat", "lang"]) {
        if (active[kind]) params.set(param[kind], active[kind]); else params.delete(param[kind]);
      }
      const query = params.toString();
      history.replaceState(null, "", location.pathname + (query ? "?" + query : "") + location.hash);
    };
    const set = (kind, value) => {
      active[kind] = value || null;
      apply();
      saveUrl();
    };

    for (const control of Object.values(controls)) {
      if (control.select) {
        control.el.addEventListener("change", () => set(control.kind, control.el.value));
        continue;
      }
      control.el.addEventListener("click", e => {
        const button = e.target.closest("button[data-value]");
        if (!button || button.getAttribute("aria-disabled") === "true") return;
        const value = button.dataset.value || null;   // "" is the "all" chip
        set(control.kind, value === null || value === active[control.kind] ? null : value);
      });
    }
    clear.addEventListener("click", () => {
      active.cat = active.lang = null;
      apply();
      saveUrl();
    });

    // Load state from the URL; unknown values are ignored. A combination with no
    // matches still loads: the empty message shows and the controls can clear it.
    const params = new URLSearchParams(location.search);
    for (const [kind, control] of Object.entries(controls)) {
      const value = params.get(param[kind]);
      if (value && control.items.some(i => i.dataset.value === value)) active[kind] = value;
    }

    // Sidebar layout: the lists sit in a column beside the grid, shown or hidden by one
    // icon button at the start of the toolbar (a panel above the grid on narrow screens,
    // where the button also says "ფილტრები"). The long language list folds after the top
    // entries; while the column is hidden, the active filters show as removable tags.
    const side = filters.querySelector(".devs-side");
    if (side) {
      const toggle = filters.querySelector(".devs-toggle");
      const badge = toggle.querySelector(".devs-toggle-badge");
      const tags = filters.querySelector(".devs-tags");
      side.hidden = toggle.hidden = false;
      filters.classList.add("ready");

      // "ყველა ენა…" / "ნაკლები": folded, the list keeps its top entries plus the active one.
      for (const facet of side.querySelectorAll(".devs-facet")) {
        const more = facet.querySelector(".devs-facet-more");
        if (!more) continue;
        const fold = () => {
          const open = more.getAttribute("aria-expanded") === "true";
          for (const li of facet.querySelectorAll(".devs-facet-extra"))
            li.hidden = !open && li.querySelector("button").getAttribute("aria-pressed") !== "true";
          more.textContent = t(open ? "devs.fewerLanguages" : "devs.allLanguages");
        };
        more.addEventListener("click", () => {
          more.setAttribute("aria-expanded", String(more.getAttribute("aria-expanded") !== "true"));
          fold();
        });
        afterApply.push(fold);
      }

      // Desktop: the column is shown or hidden (default hidden, the choice remembered).
      // Narrow screens: the panel opens under the toolbar and closes with the button,
      // ×, "მზადაა" or Esc.
      const narrow = matchMedia("(max-width: 56rem)");
      const shown = () => narrow.matches ? filters.classList.contains("open") : !root.classList.contains("devs-collapsed");
      const activeCount = () => ["cat", "lang"].filter(kind => active[kind]).length;
      const sync = () => {
        const open = shown();
        const n = activeCount();
        toggle.setAttribute("aria-expanded", String(open));
        const action = t(open ? "devs.hideFilters" : "devs.showFilters");
        toggle.title = action;
        toggle.setAttribute("aria-label", n ? `${action} (${n})` : action);
        badge.textContent = n || "";
        badge.hidden = !n;
      };
      const setCollapsed = collapsed => {
        root.classList.toggle("devs-collapsed", collapsed);
        try { localStorage.setItem(sidebarKey, collapsed ? "collapsed" : "expanded"); } catch { /* private mode: fine */ }
        sync();
      };
      const openPanel = open => {
        filters.classList.toggle("open", open);
        sync();
      };
      toggle.addEventListener("click", () => {
        if (narrow.matches) openPanel(!filters.classList.contains("open"));
        else setCollapsed(!root.classList.contains("devs-collapsed"));
      });
      for (const b of side.querySelectorAll("[data-panel-close]"))
        b.addEventListener("click", () => { openPanel(false); toggle.focus(); });
      side.addEventListener("keydown", e => {
        if (e.key === "Escape" && filters.classList.contains("open")) { openPanel(false); toggle.focus(); }
      });
      narrow.addEventListener?.("change", sync);

      // The "×" on a tag: the same small SVG as the panel's close button (a text ✕
      // renders from different fonts, even in colour, next to Georgian and Latin).
      const closeIcon = () => {
        const ns = "http://www.w3.org/2000/svg";
        const svg = document.createElementNS(ns, "svg");
        svg.setAttribute("class", "icon-x");
        svg.setAttribute("viewBox", "0 0 12 12");
        svg.setAttribute("aria-hidden", "true");
        svg.setAttribute("focusable", "false");
        const path = document.createElementNS(ns, "path");
        path.setAttribute("d", "M3 3l6 6M9 3L3 9");
        svg.append(path);
        return svg;
      };

      afterApply.push(() => {
        tags.replaceChildren(...["cat", "lang"].filter(kind => active[kind]).map(kind => {
          const tag = document.createElement("button");
          tag.type = "button";
          tag.className = "devs-tag";
          tag.textContent = label(kind, active[kind]);
          tag.append(closeIcon());
          tag.setAttribute("aria-label", t("devs.removeFilter", label(kind, active[kind])));
          tag.addEventListener("click", () => { set(kind, null); toggle.focus(); });
          return tag;
        }));
        sync();
      });
    }

    // Sorting regroups or merges the lists; re-apply so group headings stay right.
    filters.querySelector(".sort[data-sort-for]")?.addEventListener("sorted", apply);
    languageHooks.push(apply);
    apply();

    // Chip rows scroll sideways: center a chip selected by the URL, and fade the right
    // edge while more chips lie beyond it.
    for (const row of filters.querySelectorAll(".filter-row")) {
      const on = row.querySelector('button[aria-pressed="true"]');
      if (on && row.scrollWidth > row.clientWidth)
        row.scrollLeft = Math.max(0, on.offsetLeft - row.offsetLeft - (row.clientWidth - on.offsetWidth) / 2);
      const fade = () => row.classList.toggle("fade-end", row.scrollLeft + row.clientWidth < row.scrollWidth - 2);
      row.addEventListener("scroll", fade, { passive: true });
      window.addEventListener("resize", fade);
      fade();
    }
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
