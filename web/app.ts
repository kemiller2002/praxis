// Purely functional, framework-free client for the ROS work backlog API.
//
// The flow is always: DOM event -> command (fetch) -> server (the kernel,
// via ros_cli.mjs) -> updated data -> render(state) -> DOM. The DOM is a
// projection of `state`; nothing here writes to `state` except `setState`,
// and nothing writes to the DOM except `render` and the dialog helpers.
// There is no two-way binding: typing in the filter inputs never mutates
// `state` directly, it triggers a command that re-fetches and re-renders.
//
// Validation (is this transition legal? is a reason required?) lives only
// on the server, which itself only calls into ros_cli.mjs -- this file does
// not duplicate any of those rules; it surfaces whatever the server says.

type LiveWorkItem = {
  state: string;
  semanticState: string;
  allowedActions: string[];
};

type WorkRow = {
  id: string;
  title: string;
  tags: string[];
  priority: "high" | "medium" | "low" | null;
  status: string;
  blockedReason?: string;
  backlogActions: string[];
  liveWorkItem: LiveWorkItem | null;
  detail?: string | null;
};

type Filter = {
  tag: string;
  status: string;
};

type State = {
  readonly rows: readonly WorkRow[];
  readonly filter: Filter;
  readonly selectedId: string | null;
  readonly error: string | null;
};

const initialState: State = Object.freeze({
  rows: [],
  filter: { tag: "", status: "" },
  selectedId: null,
  error: null
});

let state: State = initialState;

function setState(patch: Partial<State>): void {
  state = { ...state, ...patch };
  render(state);
}

// ---------------------------------------------------------------------------
// Effects: the only functions in this file that talk to the network.
// ---------------------------------------------------------------------------

async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) }
  });
  const body: unknown = await response.json();
  if (!response.ok) {
    const message = typeof body === "object" && body && "error" in body ? String((body as { error: unknown }).error) : `request failed (${response.status})`;
    throw new Error(message);
  }
  return body as T;
}

function buildQuery(filter: Filter): string {
  const params = new URLSearchParams();
  if (filter.tag.trim()) {
    for (const tag of filter.tag.split(",").map((value) => value.trim()).filter(Boolean)) {
      params.append("tag", tag);
    }
  }
  if (filter.status) params.set("status", filter.status);
  const query = params.toString();
  return query ? `?${query}` : "";
}

type RepositoryStatus = { repository: string; protocolVersion: string; validation: string };

const api = {
  list: (filter: Filter): Promise<WorkRow[]> => apiRequest(`/api/work${buildQuery(filter)}`),
  status: (): Promise<RepositoryStatus> => apiRequest("/api/status"),
  add: (input: { title: string; tags: string[]; priority: string }): Promise<WorkRow> =>
    apiRequest("/api/work", { method: "POST", body: JSON.stringify(input) }),
  ready: (id: string): Promise<WorkRow> => apiRequest(`/api/work/${encodeURIComponent(id)}/ready`, { method: "POST" }),
  block: (id: string, reason: string): Promise<WorkRow> =>
    apiRequest(`/api/work/${encodeURIComponent(id)}/block`, { method: "POST", body: JSON.stringify({ reason }) }),
  abandon: (id: string, reason: string): Promise<WorkRow> =>
    apiRequest(`/api/work/${encodeURIComponent(id)}/abandon`, { method: "POST", body: JSON.stringify({ reason }) }),
  start: (id: string, type: string): Promise<WorkRow> =>
    apiRequest(`/api/work/${encodeURIComponent(id)}/start`, { method: "POST", body: JSON.stringify({ type }) }),
  resume: (id: string): Promise<WorkRow> => apiRequest(`/api/work/${encodeURIComponent(id)}/resume`, { method: "POST" }),
  complete: (id: string, evidence: { type: string; path: string }[], conclusion: string | null): Promise<WorkRow> =>
    apiRequest(`/api/work/${encodeURIComponent(id)}/complete`, {
      method: "POST",
      body: JSON.stringify({ evidence, conclusion })
    })
};

// ---------------------------------------------------------------------------
// Pure rendering: (data) -> DOM nodes. None of these functions perform
// network effects or read mutable module state directly -- everything they
// need arrives as a parameter.
// ---------------------------------------------------------------------------

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  attrs: Record<string, string> = {},
  children: (Node | string)[] = []
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, value);
  for (const child of children) node.append(child);
  return node;
}

function renderTags(tags: readonly string[]): HTMLElement {
  return el("span", {}, tags.map((tag) => el("span", { class: "tag" }, [tag])));
}

function statusPill(status: string): HTMLElement {
  return el("span", { class: `status-pill status-${status}` }, [status]);
}

type RowAction = { label: string; run: () => void };

function actionsForRow(row: WorkRow): RowAction[] {
  const actions: RowAction[] = [{ label: "Show", run: () => selectRow(row.id) }];

  if (row.liveWorkItem) {
    for (const action of row.liveWorkItem.allowedActions) {
      if (action === "block") actions.push({ label: "Block", run: () => void runBlock(row.id) });
      if (action === "resume") actions.push({ label: "Resume", run: () => void runResume(row.id) });
      if (action === "complete") actions.push({ label: "Complete", run: () => void runComplete(row.id) });
    }
    return actions;
  }

  for (const action of row.backlogActions) {
    if (action === "ready") actions.push({ label: "Mark ready", run: () => void runReady(row.id) });
    if (action === "block") actions.push({ label: "Block", run: () => void runBlock(row.id) });
    if (action === "start") actions.push({ label: "Start", run: () => void runStart(row.id) });
    if (action === "abandon") actions.push({ label: "Abandon", run: () => void runAbandon(row.id) });
  }
  return actions;
}

function renderRow(row: WorkRow): HTMLTableRowElement {
  const actionsCell = el("td", { class: "row-actions" });
  for (const action of actionsForRow(row)) {
    const button = el("button", { type: "button" }, [action.label]);
    button.addEventListener("click", action.run);
    actionsCell.append(button);
  }
  return el("tr", { "data-id": row.id }, [
    el("td", {}, [row.id]),
    el("td", {}, [row.title]),
    el("td", {}, [statusPill(row.status)]),
    el("td", {}, [renderTags(row.tags)]),
    el("td", {}, [row.priority ?? ""]),
    actionsCell
  ]);
}

function renderTable(rows: readonly WorkRow[]): void {
  const body = document.getElementById("work-table-body");
  if (!(body instanceof HTMLTableSectionElement)) throw new Error("work-table-body missing");
  body.replaceChildren(...rows.map(renderRow));
}

function renderError(message: string | null): void {
  const node = document.getElementById("error");
  if (!node) return;
  if (!message) { node.hidden = true; node.textContent = ""; return; }
  node.hidden = false;
  node.textContent = message;
}

function renderDetail(rows: readonly WorkRow[], selectedId: string | null): void {
  const section = document.getElementById("detail");
  const body = document.getElementById("detail-body");
  if (!section || !body) return;
  const row = selectedId ? rows.find((candidate) => candidate.id === selectedId) ?? null : null;
  if (!row) { section.hidden = true; body.textContent = ""; return; }
  section.hidden = false;
  const summary = JSON.stringify(row, null, 2);
  body.textContent = row.detail ? `${summary}\n\n${row.detail}` : summary;
}

function render(current: State): void {
  renderTable(current.rows);
  renderError(current.error);
  renderDetail(current.rows, current.selectedId);
}

// ---------------------------------------------------------------------------
// Dialog helpers: native <dialog> + <form method="dialog"> already closes
// itself and sets `returnValue` to the clicked button's `value` attribute --
// no JS is needed for that part. These functions only open the dialog,
// reset its fields, and read back whatever the user entered once it closes.
// ---------------------------------------------------------------------------

function dialogEl(id: string): HTMLDialogElement {
  const node = document.getElementById(id);
  if (!(node instanceof HTMLDialogElement)) throw new Error(`missing dialog #${id}`);
  return node;
}

function waitForClose(dialog: HTMLDialogElement): Promise<string> {
  return new Promise((resolve) => {
    dialog.addEventListener("close", () => resolve(dialog.returnValue), { once: true });
  });
}

async function promptReason(title: string): Promise<string | null> {
  const dialog = dialogEl("reason-dialog");
  const titleNode = document.getElementById("reason-dialog-title");
  const input = document.getElementById("reason-dialog-input");
  if (titleNode) titleNode.textContent = title;
  if (input instanceof HTMLTextAreaElement) input.value = "";
  dialog.showModal();
  const result = await waitForClose(dialog);
  if (result !== "confirm") return null;
  return input instanceof HTMLTextAreaElement ? input.value : "";
}

async function promptStartType(): Promise<string | null> {
  const dialog = dialogEl("start-dialog");
  const select = document.getElementById("start-dialog-type");
  dialog.showModal();
  const result = await waitForClose(dialog);
  if (result !== "confirm") return null;
  return select instanceof HTMLSelectElement ? select.value : "feature";
}

function addEvidenceRow(container: HTMLElement): void {
  container.append(el("div", { class: "evidence-row" }, [
    el("input", { placeholder: "type (e.g. implementation)", "data-role": "evidence-type" }),
    el("input", { placeholder: "path", "data-role": "evidence-path" })
  ]));
}

type CompletionInput = { evidence: { type: string; path: string }[]; conclusion: string | null };

async function promptCompletion(): Promise<CompletionInput | null> {
  const dialog = dialogEl("complete-dialog");
  const rows = document.getElementById("complete-evidence-rows");
  const conclusionInput = document.getElementById("complete-conclusion");
  const addButton = document.getElementById("complete-add-row");
  if (!rows) throw new Error("missing complete-evidence-rows");

  rows.replaceChildren();
  addEvidenceRow(rows);
  addEvidenceRow(rows);
  if (conclusionInput instanceof HTMLInputElement) conclusionInput.value = "";

  const onAdd = (): void => addEvidenceRow(rows);
  addButton?.addEventListener("click", onAdd);
  dialog.showModal();
  const result = await waitForClose(dialog);
  addButton?.removeEventListener("click", onAdd);
  if (result !== "confirm") return null;

  const evidence: { type: string; path: string }[] = [];
  for (const row of Array.from(rows.children)) {
    const typeInput = row.querySelector('[data-role="evidence-type"]');
    const pathInput = row.querySelector('[data-role="evidence-path"]');
    const type = typeInput instanceof HTMLInputElement ? typeInput.value.trim() : "";
    const evidencePath = pathInput instanceof HTMLInputElement ? pathInput.value.trim() : "";
    if (type && evidencePath) evidence.push({ type, path: evidencePath });
  }
  const conclusion = conclusionInput instanceof HTMLInputElement ? conclusionInput.value.trim() : "";
  return { evidence, conclusion: conclusion || null };
}

// ---------------------------------------------------------------------------
// Commands: the only functions allowed to call setState, and only ever
// after an effect (a fetch) has resolved or failed.
// ---------------------------------------------------------------------------

async function refresh(): Promise<void> {
  try {
    const rows = await api.list(state.filter);
    setState({ rows, error: null });
  } catch (error) {
    setState({ error: (error as Error).message });
  }
}

async function runWithRefresh(action: () => Promise<unknown>): Promise<void> {
  try {
    await action();
    await refresh();
  } catch (error) {
    setState({ error: (error as Error).message });
  }
}

function selectRow(id: string): void {
  setState({ selectedId: state.selectedId === id ? null : id });
}

function runReady(id: string): Promise<void> {
  return runWithRefresh(() => api.ready(id));
}

function runResume(id: string): Promise<void> {
  return runWithRefresh(() => api.resume(id));
}

async function runBlock(id: string): Promise<void> {
  const reason = await promptReason("Reason for blocking");
  if (reason === null) return;
  await runWithRefresh(() => api.block(id, reason));
}

async function runAbandon(id: string): Promise<void> {
  const reason = await promptReason("Reason for abandoning");
  if (reason === null) return;
  await runWithRefresh(() => api.abandon(id, reason));
}

async function runStart(id: string): Promise<void> {
  const type = await promptStartType();
  if (type === null) return;
  await runWithRefresh(() => api.start(id, type));
}

async function runComplete(id: string): Promise<void> {
  const input = await promptCompletion();
  if (input === null) return;
  await runWithRefresh(() => api.complete(id, input.evidence, input.conclusion));
}

function parseTags(raw: string): string[] {
  return raw.split(",").map((tag) => tag.trim()).filter(Boolean);
}

function wireAddForm(): void {
  const form = document.getElementById("add-form");
  const titleInput = document.getElementById("add-title");
  const tagsInput = document.getElementById("add-tags");
  const priorityInput = document.getElementById("add-priority");
  if (!(form instanceof HTMLFormElement)) throw new Error("add-form missing");

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const title = titleInput instanceof HTMLInputElement ? titleInput.value.trim() : "";
    const tags = parseTags(tagsInput instanceof HTMLInputElement ? tagsInput.value : "");
    const priority = priorityInput instanceof HTMLSelectElement ? priorityInput.value : "medium";
    if (!title) return;
    void runWithRefresh(() => api.add({ title, tags, priority })).then(() => form.reset());
  });
}

function wireFilters(): void {
  const tagInput = document.getElementById("filter-tag");
  const statusSelect = document.getElementById("filter-status");
  const clearButton = document.getElementById("filter-clear");

  tagInput?.addEventListener("input", () => {
    const value = tagInput instanceof HTMLInputElement ? tagInput.value : "";
    setState({ filter: { ...state.filter, tag: value } });
    void refresh();
  });

  statusSelect?.addEventListener("change", () => {
    const value = statusSelect instanceof HTMLSelectElement ? statusSelect.value : "";
    setState({ filter: { ...state.filter, status: value } });
    void refresh();
  });

  clearButton?.addEventListener("click", () => {
    if (tagInput instanceof HTMLInputElement) tagInput.value = "";
    if (statusSelect instanceof HTMLSelectElement) statusSelect.value = "";
    setState({ filter: { tag: "", status: "" } });
    void refresh();
  });
}

async function showRepositoryLabel(): Promise<void> {
  const node = document.getElementById("repository-label");
  if (!node) return;
  try {
    const status = await api.status();
    node.textContent = `${status.repository} · protocol ${status.protocolVersion} · validation ${status.validation}`;
  } catch {
    node.textContent = "";
  }
}

function main(): void {
  wireAddForm();
  wireFilters();
  render(state);
  void refresh();
  void showRepositoryLabel();
}

main();
