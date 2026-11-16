const element = (id) => document.getElementById(id);
const pageIds = ["dashboard", "tasks", "activity", "models", "settings"];

function showPage(pageId, title) {
    for (const id of pageIds) {
        element(`page-${id}`).classList.toggle("hidden", id !== pageId);
    }

    document.querySelectorAll("[data-page]").forEach((button) => {
        button.classList.toggle("active", button.dataset.page === pageId);
    });

    element("page-title").textContent = title;
}

document.querySelectorAll("[data-page]").forEach((button) => {
    button.addEventListener("click", () => showPage(button.dataset.page, button.textContent.trim()));
});

document.querySelectorAll("[data-open-page]").forEach((button) => {
    button.addEventListener("click", () => showPage(button.dataset.openPage, "Задачи"));
});

element("sidebar-toggle").addEventListener("click", () => {
    document.querySelector(".app-shell").classList.toggle("sidebar-collapsed");
});

function formatDate(value) {
    if (!value) return "—";
    return new Date(value).toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" });
}

function appendCell(row, value) {
    const cell = document.createElement("td");
    cell.textContent = value ?? "—";
    row.append(cell);
}

function renderTasks(tasks, target) {
    target.replaceChildren();

    if (!tasks.length) {
        const row = document.createElement("tr");
        const cell = document.createElement("td");
        cell.className = "empty-row";
        cell.colSpan = 5;
        cell.textContent = "Задач пока нет";
        row.append(cell);
        target.append(row);
        return;
    }

    for (const task of tasks) {
        const row = document.createElement("tr");
        appendCell(row, task.title);
        appendCell(row, task.status);
        appendCell(row, task.priority);
        appendCell(row, task.workspaceId || "—");
        appendCell(row, formatDate(task.updatedUtc));
        target.append(row);
    }
}

function renderProfiles(settings) {
    const container = element("profiles");
    container.replaceChildren();

    for (const profile of settings.profiles ?? []) {
        const card = document.createElement("article");
        card.className = "profile-card";

        const name = document.createElement("strong");
        name.textContent = profile.name;
        card.append(name);

        const provider = document.createElement("p");
        provider.textContent = `${profile.providerId} · ${profile.modelId}`;
        card.append(provider);

        const endpoint = document.createElement("p");
        endpoint.textContent = profile.baseUrl;
        card.append(endpoint);

        if (profile.id === settings.activeProfileId) {
            const active = document.createElement("p");
            active.className = "profile-active";
            active.textContent = "● Активный профиль";
            card.append(active);
        }

        container.append(card);
    }
}

function renderWorkspaces(settings) {
    const container = element("workspaces");
    container.replaceChildren();

    for (const workspace of settings.workspaces ?? []) {
        const entry = document.createElement("div");
        entry.className = "workspace-entry";

        const label = document.createElement("span");
        label.textContent = workspace.isDefault ? `${workspace.id} · По умолчанию` : workspace.id;
        entry.append(label, document.createTextNode(workspace.path));
        container.append(entry);
    }
}

async function fetchJson(url) {
    const response = await fetch(url, { cache: "no-store" });
    if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
    return response.json();
}

async function loadDashboard() {
    try {
        const [dashboard, tasks, settings] = await Promise.all([
            fetchJson("/api/dashboard"),
            fetchJson("/api/tasks"),
            fetchJson("/api/settings")
        ]);

        element("online").innerHTML = '<span class="indicator-dot"></span>Online';
        element("agent-state").textContent = "Агент запущен";
        element("completed-today").textContent = dashboard.today.completed;
        element("activity").textContent = dashboard.activityPercent == null ? "—" : `${dashboard.activityPercent}%`;
        element("errors").textContent = dashboard.errorsToday ?? "—";
        element("fixes").textContent = dashboard.fixesToday ?? "—";
        element("model-provider").textContent = dashboard.model?.providerName ?? "Не выбран";
        element("model-id").textContent = dashboard.model?.modelId ?? "Модель не указана";
        element("count-inbox").textContent = dashboard.totals.inbox;
        element("count-queue").textContent = dashboard.totals.queued;
        element("count-done").textContent = dashboard.totals.completed;
        element("count-failed").textContent = dashboard.totals.failed;

        if (dashboard.currentTask) {
            element("current-title").textContent = dashboard.currentTask.title;
            element("current-meta").textContent = `ID: ${dashboard.currentTask.id} · ${dashboard.currentTask.workspaceId || "Workspace не указан"}`;
            element("task-state").textContent = "Выполняется";
        } else {
            element("current-title").textContent = "Ожидание новой задачи";
            element("current-meta").textContent = "Агент ожидает работу из очереди";
            element("task-state").textContent = "Ожидание";
        }

        renderTasks(tasks.slice(0, 5), element("recent-tasks-body"));
        renderTasks(tasks, element("tasks-body"));
        element("task-count").textContent = `${tasks.length} задач`;
        element("nav-task-count").textContent = tasks.length;
        renderProfiles(settings);
        renderWorkspaces(settings);
        element("vault-path").textContent = settings.obsidian.vaultPath || "Не настроен";
    } catch (error) {
        element("online").innerHTML = '<span class="indicator-dot"></span>Offline';
        element("agent-state").textContent = "API недоступен";
        console.error("Не удалось обновить дашборд:", error);
    }
}

element("refresh").addEventListener("click", loadDashboard);
loadDashboard();
window.setInterval(loadDashboard, 5000);
