const $ = (id) => document.getElementById(id);
const pageIds = ["dashboard", "tasks", "queue", "activity", "workspaces", "logs", "models", "settings"];
const state = { tasks: [], settings: null, dashboard: null, activity: [], filteredLogs: null, loading: false, lastUpdated: null, retryingTasks: new Set(), apiOnline: false };

function showPage(pageId, title) {
    for (const id of pageIds) $(`page-${id}`).classList.toggle("hidden", id !== pageId);
    document.querySelectorAll("[data-page]").forEach((button) => {
        const active = button.dataset.page === pageId;
        button.classList.toggle("active", active);
        button.setAttribute("aria-current", active ? "page" : "false");
    });
    $("page-title").textContent = title;
    document.querySelector(".main-content").scrollTo({ top: 0, behavior: "smooth" });
}

const pageTitles = { dashboard: "Дашборд", tasks: "Задачи", queue: "Очередь", activity: "Активность", workspaces: "Рабочие пространства", logs: "Журнал", models: "Модели и провайдеры", settings: "Настройки" };
document.querySelectorAll("[data-page]").forEach((button) => button.addEventListener("click", () => showPage(button.dataset.page, pageTitles[button.dataset.page] || button.dataset.title || button.textContent.trim())));
document.querySelectorAll("[data-open-page]").forEach((button) => button.addEventListener("click", () => { const id = button.dataset.openPage; showPage(id, pageTitles[id] || id); }));
const sidebarToggle = $("sidebar-toggle");
const appShell = document.querySelector(".app-shell");
sidebarToggle.addEventListener("click", () => {
    const collapsed = appShell.classList.toggle("sidebar-collapsed");
    sidebarToggle.textContent = collapsed ? "›" : "‹";
    sidebarToggle.setAttribute("aria-expanded", String(!collapsed));
    sidebarToggle.setAttribute("aria-label", collapsed ? "Раскрыть меню" : "Свернуть меню");
    sidebarToggle.title = collapsed ? "Раскрыть меню" : "Свернуть меню";
});

function formatDate(value) {
    if (!value) return "—";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" });
}
function addCell(row, value, className = "") {
    const cell = document.createElement("td");
    cell.textContent = value ?? "—";
    if (className) cell.className = className;
    row.append(cell);
}
function renderTasks(tasks, target, editable = false) {
    target.replaceChildren();
    if (!tasks.length) {
        const row = document.createElement("tr");
        const cell = document.createElement("td");
        cell.className = "empty-row"; cell.colSpan = editable ? 6 : 6; cell.textContent = "Задач по этому фильтру нет";
        row.append(cell); target.append(row); return;
    }
    for (const task of tasks) {
        const row = document.createElement("tr");
        row.className = "task-row";
        addCell(row, task.title, "task-title-cell");
        const status = document.createElement("td");
        const pill = document.createElement("span"); pill.className = `status-pill status-${String(task.status).toLowerCase()}`; pill.textContent = task.status;
        status.append(pill); row.append(status);
        addCell(row, task.priority);
        addCell(row, task.workspaceId || "—");
        addCell(row, formatDate(task.updatedUtc));
        const action = document.createElement("td");
        if (editable) {
            const button = document.createElement("button"); button.type = "button"; button.className = "row-action"; button.textContent = "Изменить";
            button.addEventListener("click", () => openTaskEditor(task)); action.append(button);
        }
        row.append(action); target.append(row);
    }
}
const eventLabels = {
    agent_started: "Агент запущен", inbox_accepted: "Получена новая задача", planning_completed: "Планирование завершено",
    planning_failed: "Ошибка планирования", task_started: "Задача взята в работу", task_completed: "Задача завершена",
    task_failed: "Задача не выполнена", task_error: "Ошибка выполнения", execution_attempt: "Попытка выполнения",
    tool_started: "Вызов инструмента", tool_succeeded: "Инструмент выполнен", tool_failed: "Ошибка инструмента",
    validation_passed: "Проверка пройдена", validation_failed: "Проверка не пройдена",
    correction_started: "Запущено исправление", correction_succeeded: "Исправление успешно", task_rollback: "Изменения отменены",
    runtime_ready: "Исполнитель готов", runtime_cycle_error: "Ошибка рабочего цикла"
};
function renderActivity(target, events, limit = 100) {
    target.replaceChildren();
    const items = events.slice(0, limit);
    if (!items.length) {
        const empty = document.createElement("div"); empty.className = "activity-empty"; empty.textContent = "Событий пока нет. Они появятся при работе агента."; target.append(empty); return;
    }
    for (const item of items) {
        const row = document.createElement("article"); row.className = `activity-item severity-${item.severity || "info"}`;
        const marker = document.createElement("span"); marker.className = "activity-marker";
        const content = document.createElement("div"); content.className = "activity-content";
        const heading = document.createElement("div"); heading.className = "activity-heading";
        const title = document.createElement("strong"); title.textContent = eventLabels[item.type] || item.title || item.type;
        const time = document.createElement("time"); time.dateTime = item.timestampUtc; time.textContent = formatDate(item.timestampUtc);
        heading.append(title, time);
        const description = document.createElement("p"); description.textContent = item.description || item.title || "";
        content.append(heading, description);
        if (item.taskId) { const task = document.createElement("small"); task.textContent = `Задача: ${item.taskId}`; content.append(task); }
        row.append(marker, content); target.append(row);
    }
}
function renderProfiles(settings) {
    const container = $("profiles"); container.replaceChildren();
    for (const profile of settings.profiles ?? []) {
        const card = document.createElement("article"); card.className = "profile-card";
        const name = document.createElement("strong"); name.textContent = profile.name; card.append(name);
        const provider = document.createElement("p"); provider.textContent = `${profile.providerId} · ${profile.modelId}`; card.append(provider);
        const endpoint = document.createElement("p"); endpoint.textContent = profile.baseUrl; card.append(endpoint);
        if (profile.id === settings.activeProfileId) { const active = document.createElement("p"); active.className = "profile-active"; active.textContent = "● Активный профиль"; card.append(active); }
        container.append(card);
    }
}
function renderWorkspaces(settings) {
    const container = $("workspaces"); container.replaceChildren();
    const cards = $("workspace-cards"); if (cards) cards.replaceChildren();
    for (const workspace of settings.workspaces ?? []) {
        const entry = document.createElement("div"); entry.className = "workspace-entry";
        const label = document.createElement("span"); label.textContent = workspace.isDefault ? `${workspace.id} · По умолчанию` : workspace.id;
        const path = document.createElement("div"); path.textContent = workspace.path;
        entry.append(label, path); container.append(entry);
        if (cards) {
            const card = document.createElement("article"); card.className = "workspace-card";
            const head = document.createElement("div"); head.className = "workspace-card-head";
            const name = document.createElement("strong"); name.textContent = workspace.id;
            const badge = document.createElement("span"); badge.className = `workspace-state ${workspace.exists ? "is-ready" : "is-missing"}`; badge.textContent = workspace.exists ? "Каталог доступен" : "Каталог не найден";
            head.append(name, badge);
            const location = document.createElement("p"); location.className = "workspace-path"; location.textContent = workspace.path;
            const stats = document.createElement("div"); stats.className = "workspace-card-stats";
            const count = document.createElement("strong"); count.textContent = String(workspace.taskCount ?? 0);
            const caption = document.createElement("span"); caption.textContent = "задач связано с Workspace";
            stats.append(count, caption); card.append(head, location, stats);
            if (workspace.isDefault) { const def = document.createElement("span"); def.className = "workspace-default-tag"; def.textContent = "По умолчанию"; card.append(def); }
            cards.append(card);
        }
    }
    if (cards && !(settings.workspaces ?? []).length) { const empty = document.createElement("div"); empty.className = "activity-empty"; empty.textContent = "Рабочие пространства не зарегистрированы в settings.json"; cards.append(empty); }
}
async function fetchJson(url, options) {
    const response = await fetch(url, { cache: "no-store", ...options });
    if (!response.ok) {
        let message = `${url}: HTTP ${response.status}`;
        try { const body = await response.json(); if (body.error) message = body.error; } catch { /* non-JSON response */ }
        throw new Error(message);
    }
    return response.status === 204 ? null : response.json();
}
function renderDashboard() {
    const d = state.dashboard, tasks = state.tasks, settings = state.settings;
    if (!d || !settings) return;
    $("online").innerHTML = '<span class="indicator-dot"></span>Online';
    $("agent-state").textContent = "Агент запущен";
    $("completed-today").textContent = d.today.completed;
    $("activity").textContent = d.eventsToday ?? 0;
    $("errors").textContent = d.errorsToday ?? "—"; $("fixes").textContent = d.fixesToday ?? "—";
    $("model-provider").textContent = d.model?.providerName ?? "Не выбран";
    $("model-id").textContent = d.model?.modelId ?? "Модель не указана";
    $("count-inbox").textContent = d.totals.inbox; $("count-queue").textContent = d.totals.queued;
    $("count-done").textContent = d.totals.completed; $("count-failed").textContent = d.totals.failed;
    if (d.currentTask) {
        $("current-title").textContent = d.currentTask.title;
        $("current-meta").textContent = `ID: ${d.currentTask.id} · ${d.currentTask.workspaceId || "Workspace не указан"}`;
        $("task-state").textContent = "Выполняется";
        $("task-progress").classList.add("is-active");
        const stage = d.currentActivity;
        $("task-progress-label").textContent = stage ? `${eventLabels[stage.type] || stage.title}: ${stage.description || ""}` : "Задача выполняется";
    } else {
        $("current-title").textContent = "Ожидание новой задачи";
        $("current-meta").textContent = "Агент ожидает работу из очереди";
        $("task-state").textContent = "Ожидание";
        $("task-progress").classList.remove("is-active");
        $("task-progress-label").textContent = "Агент ожидает новую задачу";
    }
    renderTasks(tasks.slice(0, 5), $("recent-tasks-body"));
    renderActivity($("dashboard-activity"), state.activity, 6);
    renderActivity($("activity-list"), state.activity);
    $("activity-count").textContent = `${state.activity.length} событий`;
    applyQueueFilters();
    applyLogFilters();
    applyTaskFilters();
    $("task-count").textContent = `${tasks.length} задач`;
    $("nav-task-count").textContent = tasks.length;
    $("nav-queue-count").textContent = tasks.filter(task => task.status === "Queue" || task.status === "InProgress").length;
    renderQueue(tasks);
    renderProfiles(settings); renderWorkspaces(settings);
    $("vault-path").textContent = settings.obsidian.vaultPath || "Не настроен";
    $("workspace-name").textContent = settings.workspaces.find(w => w.isDefault)?.id ?? "Workspace";
}
function applyTaskFilters() {
    const query = ($("task-search").value || "").trim().toLocaleLowerCase("ru");
    const status = $("task-status-filter").value;
    const filtered = state.tasks.filter(task => {
        const matchesQuery = !query || [task.title, task.id, task.workspaceId, task.description].some(value => String(value ?? "").toLocaleLowerCase("ru").includes(query));
        return matchesQuery && (!status || task.status === status);
    });
    renderTasks(filtered, $("tasks-body"), true);
}
$("task-search").addEventListener("input", applyTaskFilters);
$("task-status-filter").addEventListener("change", applyTaskFilters);

function renderQueue(tasks) {
    const active = tasks.filter(task => ["Queue", "InProgress", "Failed"].includes(task.status));
    $("queue-waiting").textContent = tasks.filter(task => task.status === "Queue").length;
    $("queue-running").textContent = tasks.filter(task => task.status === "InProgress").length;
    $("queue-failed").textContent = tasks.filter(task => task.status === "Failed").length;
    $("queue-count").textContent = `${active.length} задач`;
    applyQueueFilters();
}
function applyQueueFilters() {
    const target = $("queue-body"); if (!target) return;
    const query = ($("queue-search").value || "").trim().toLocaleLowerCase("ru");
    const status = $("queue-status-filter").value;
    const filtered = state.tasks.filter(task => ["Queue", "InProgress", "Failed"].includes(task.status) && (!status || task.status === status) && (!query || [task.title, task.id, task.workspaceId].some(value => String(value ?? "").toLocaleLowerCase("ru").includes(query))));
    target.replaceChildren();
    if (!filtered.length) { const row = document.createElement("tr"), cell = document.createElement("td"); cell.className = "empty-row"; cell.colSpan = 5; cell.textContent = "В очереди нет задач по выбранным условиям"; row.append(cell); target.append(row); return; }
    for (const task of filtered) {
        const row = document.createElement("tr"); row.className = "task-row"; addCell(row, task.title, "task-title-cell");
        const statusCell = document.createElement("td"), pill = document.createElement("span"); pill.className = `status-pill status-${String(task.status).toLowerCase()}`; pill.textContent = task.status === "Queue" ? "Ожидает" : task.status === "InProgress" ? "Выполняется" : "Ошибка"; statusCell.append(pill); row.append(statusCell);
        addCell(row, task.priority); addCell(row, task.workspaceId || "—"); addCell(row, formatDate(task.updatedUtc));
        const action = document.createElement("td");
        if (task.status === "Failed") {
            const retry = document.createElement("button");
            retry.type = "button";
            retry.className = "row-action retry-action";
            retry.textContent = state.retryingTasks.has(task.id) ? "Возврат…" : "Повторить";
            retry.disabled = state.retryingTasks.has(task.id);
            retry.addEventListener("click", () => retryFailedTask(task));
            action.append(retry);
        }
        row.append(action); target.append(row);
    }
}
async function retryFailedTask(task) {
    if (state.retryingTasks.has(task.id) || task.status !== "Failed") return;
    state.retryingTasks.add(task.id);
    applyQueueFilters();
    try {
        await fetchJson(`/api/tasks/${encodeURIComponent(task.id)}/retry`, { method: "POST" });
        const current = state.tasks.find(item => item.id === task.id);
        if (current) {
            current.status = "Queue";
            current.updatedUtc = new Date().toISOString();
        }
        renderDashboard();
        showToast("Задача возвращена в очередь");
        await loadDashboard(true);
    } catch (error) {
        showToast(`Не удалось повторить задачу: ${error.message}`);
    } finally {
        state.retryingTasks.delete(task.id);
        applyQueueFilters();
    }
}
$("queue-search").addEventListener("input", applyQueueFilters);
$("queue-status-filter").addEventListener("change", applyQueueFilters);

function applyLogFilters() {
    if (!$("logs-list")) return;
    const query = ($("logs-search").value || "").trim().toLocaleLowerCase("ru");
    const severity = $("logs-severity").value;
    const filtered = state.activity.filter(item => (!severity || item.severity === severity) && (!query || [item.type, item.title, item.description, item.taskId].some(value => String(value ?? "").toLocaleLowerCase("ru").includes(query))));
    $("logs-count").textContent = `${filtered.length} из ${state.activity.length} записей`;
    renderActivity($("logs-list"), filtered, 500);
}
$("logs-search").addEventListener("input", applyLogFilters);
$("logs-severity").addEventListener("change", applyLogFilters);

let editorDialog;
function ensureEditor() {
    if (editorDialog) return editorDialog;
    editorDialog = document.createElement("dialog"); editorDialog.className = "task-editor";
    editorDialog.innerHTML = `<form method="dialog" id="task-editor-form"><div class="editor-heading"><div><span class="eyebrow">TASK DETAILS</span><h2>Редактирование задачи</h2></div><button class="icon-button" value="cancel" aria-label="Закрыть">×</button></div><label>Название<input class="field" name="title" maxlength="240" required></label><label>Приоритет<select class="field" name="priority"><option value="low">Низкий</option><option value="normal">Обычный</option><option value="high">Высокий</option><option value="urgent">Срочный</option></select></label><label>Описание / контекст<textarea class="field" name="description" rows="9" maxlength="100000"></textarea></label><div class="editor-footer"><span class="editor-error" role="status"></span><button class="button-secondary" value="cancel">Отмена</button><button class="button-primary" id="task-save" type="submit" value="save">Сохранить</button></div></form>`;
    document.body.append(editorDialog);
    editorDialog.addEventListener("click", event => { if (event.target === editorDialog) editorDialog.close(); });
    return editorDialog;
}
function openTaskEditor(task) {
    const dialog = ensureEditor(), form = $("task-editor-form");
    form.elements.title.value = task.title ?? ""; form.elements.priority.value = task.priority ?? "normal";
    form.elements.description.value = task.description ?? ""; form.dataset.taskId = task.id;
    form.querySelector(".editor-error").textContent = "";
    dialog.showModal();
}
$("task-editor-form")?.addEventListener("submit", async event => event.preventDefault());
function bindEditorSubmit() {
    const form = $("task-editor-form");
    if (form.dataset.bound) return; form.dataset.bound = "1";
    form.addEventListener("submit", async event => {
        event.preventDefault();
        const save = $("task-save"), error = form.querySelector(".editor-error");
        save.disabled = true; save.textContent = "Сохранение…"; error.textContent = "";
        try {
            const id = form.dataset.taskId;
            await fetchJson(`/api/tasks/${encodeURIComponent(id)}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title: form.elements.title.value, priority: form.elements.priority.value, description: form.elements.description.value }) });
            editorDialog.close(); await loadDashboard(true); showToast("Изменения сохранены");
        } catch (e) { error.textContent = e.message; }
        finally { save.disabled = false; save.textContent = "Сохранить"; }
    });
}
function showToast(message) {
    let toast = $("app-toast"); if (!toast) { toast = document.createElement("div"); toast.id = "app-toast"; toast.className = "app-toast"; document.body.append(toast); }
    toast.textContent = message; toast.classList.add("visible"); clearTimeout(showToast.timer); showToast.timer = setTimeout(() => toast.classList.remove("visible"), 2600);
}
async function loadDashboard(silent = false) {
    if (state.loading) return;
    state.loading = true;
    if (!silent) $("refresh").classList.add("is-loading");

    try {
        // Health is independent: the shell can distinguish a dead backend from
        // a failed data endpoint and still keep the last successfully rendered state.
        const health = await fetchJson("/api/health");
        state.apiOnline = health.status === "online";
        const results = await Promise.allSettled([
            fetchJson("/api/dashboard"),
            fetchJson("/api/tasks"),
            fetchJson("/api/settings"),
            fetchJson("/api/activity?limit=500")
        ]);
        const required = results.slice(0, 3);
        const failed = required.find(result => result.status === "rejected");
        if (failed) throw failed.reason;

        state.dashboard = results[0].value;
        state.tasks = results[1].value;
        state.settings = results[2].value;
        if (results[3].status === "fulfilled") state.activity = results[3].value;
        state.lastUpdated = Date.now();
        renderDashboard();
        ensureEditor();
        bindEditorSubmit();
        $("online").innerHTML = '<span class="indicator-dot"></span>Backend online';
        $("agent-state").textContent = "Backend подключён";
    } catch (error) {
        state.apiOnline = false;
        $("online").innerHTML = '<span class="indicator-dot"></span>Offline';
        $("agent-state").textContent = "API недоступен";
        if (!silent) showToast(`Не удалось обновить данные: ${error.message}`);
        console.error("Не удалось обновить данные backend:", error);
    } finally {
        state.loading = false;
        $("refresh").classList.remove("is-loading");
    }
}
$("refresh").addEventListener("click", () => loadDashboard());
loadDashboard();
window.setInterval(() => { if (!document.hidden) loadDashboard(true); }, 5000);
document.addEventListener("visibilitychange", () => { if (!document.hidden) loadDashboard(true); });
