//централно състояние
const state = {
  currentUser: null,
  users: [],
  tickets: [],
  notifications: [],
  knownNotificationIds: new Set(),
  selectedTicketId: null,
  feedbackTicketId: null,
  statusFilter: "All",
  priorityFilter: "All",
  categoryFilter: "All",
  agentFilter: "All",
  requesterFilter: "All",
  search: "",
  adminView: "tickets",
  selectedAgentId: null,
  agentKpis: [],
  categoryTaxonomy: null,
  absences: [],
  myAbsences: [],
  calendarMonth: new Date()
};

//категориите, между които агент може да бъде специализиран (същите като в тикет формата)
const AGENT_CATEGORIES = ["Достъп и права", "Кредитни операции", "Системни инциденти", "Организационни промени"];

let notificationTimer = null;
let activeNotificationToast = null;

//enum стойности за роли
const roleLabels = {
  User: "Потребител",
  Agent: "Агент",
  Admin: "Администратор"
};

const statusLabels = {
  Open: "Отворен",
  InProgress: "В прогрес",
  Closed: "Затворен",
  Reopened: "Повторно отворен"
};

const priorityLabels = {
  Low: "Ниска - 24ч.",
  Medium: "Средна - 8ч.",
  High: "Висока - 2ч."
};

const activeStatuses = new Set(["Open", "InProgress", "Reopened"]);

const absenceTypeLabels = {
  SickLeave: "Болничен",
  PaidLeave: "Платен отпуск",
  UnpaidLeave: "Неплатен отпуск"
};

const absenceStatusLabels = {
  Pending: "Изчаква",
  Approved: "Одобрена",
  Rejected: "Отхвърлена",
  Cancelled: "Анулирана"
};

const loginView = document.querySelector("#loginView");
const appView = document.querySelector("#appView");
const loginForm = document.querySelector("#loginForm");
const loginError = document.querySelector("#loginError");
const ticketList = document.querySelector("#ticketList");
const ticketDetails = document.querySelector("#ticketDetails");
const createDialog = document.querySelector("#createDialog");
const createTicketForm = document.querySelector("#createTicketForm");
const openCreateDialogButton = document.querySelector("#openCreateDialogButton");
const feedbackDialog = document.querySelector("#feedbackDialog");
const feedbackForm = document.querySelector("#feedbackForm");
const reassignConfirmDialog = document.querySelector("#reassignConfirmDialog");
const reassignConfirmText = document.querySelector("#reassignConfirmText");
const notificationButton = document.querySelector("#notificationButton");
const notificationPanel = document.querySelector("#notificationPanel");
const notificationList = document.querySelector("#notificationList");
const markAllNotificationsReadButton = document.querySelector("#markAllNotificationsReadButton");

//функция за http заявки
async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    },
    ...options
  });

  const payload = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(payload.message || "Възникна грешка при заявката.");
  }

  return payload;
}

function formatDate(value) {
  return new Intl.DateTimeFormat("bg-BG", {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(new Date(value));
}

//изчислява дали тикета е просрочен
function formatRemaining(minutes, isOverdue) {
  if (isOverdue) {
    return "Просрочен";
  }

  if (minutes < 60) {
    return `${minutes} мин.`;
  }

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} ч.` : `${hours} ч. ${rest} мин.`;
}

function formatDuration(minutes) {
  if (minutes === null || minutes === undefined) {
    return "Няма данни";
  }

  if (minutes < 60) {
    return `${minutes} мин.`;
  }

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} ч.` : `${hours} ч. ${rest} мин.`;
}

function formatFileSize(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${Math.round(bytes / 1024)} KB`;
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function showApp() {
  loginView.classList.add("hidden");
  appView.classList.remove("hidden");
  document.querySelector("#currentUserName").textContent = state.currentUser.fullName;
  document.querySelector("#currentUserRole").textContent = roleLabels[state.currentUser.role];
  openCreateDialogButton.classList.toggle("hidden", state.currentUser.role === "Agent");
  document.querySelector("#requestAbsenceButton").classList.toggle("hidden", state.currentUser.role !== "Agent");
  document.querySelector("#agentsTabButton").classList.toggle("hidden", state.currentUser.role !== "Admin");
  document.querySelector("#openCreateAbsenceDialogButton").classList.toggle("hidden", state.currentUser.role !== "Admin");
  document.querySelectorAll(".admin-ticket-filter").forEach((item) => {
    item.classList.toggle("hidden", state.currentUser.role !== "Admin");
  });

  //без това, ако предишната сесия в същия таб (различен потребител) е останала на "агенти"/"отпуски",
  //изгледът щеше да остане скрит зад ролева проверка, без нищо да го замести
  switchAdminView("tickets");
}

function showLogin() {
  appView.classList.add("hidden");
  loginView.classList.remove("hidden");
  stopNotificationPolling();
}

async function loadDashboard() {
  const [users, ticketPayload, notifications] = await Promise.all([
    api("/api/users"),
    api("/api/tickets"),
    api("/api/notifications")
  ]);

  state.users = users;
  state.tickets = ticketPayload.tickets;
  state.notifications = notifications;
  state.knownNotificationIds = new Set(notifications.map((notification) => notification.id));
  renderAdminTicketFilters();
  updateStats(ticketPayload.stats);
  renderTicketList();
  renderTicketDetails();
  renderNotifications();
  showLatestUnreadNotification();
  startNotificationPolling();

  if (state.currentUser.role === "Admin" || state.currentUser.role === "Agent") {
    refreshAbsences().catch(() => {});
  }
}

async function refreshTickets() {
  const payload = await api("/api/tickets");
  state.tickets = payload.tickets;
  if (state.selectedTicketId && !state.tickets.some((ticket) => ticket.id === state.selectedTicketId)) {
    state.selectedTicketId = null;
  }
  updateStats(payload.stats);
  renderTicketList();
  renderTicketDetails();
}

async function refreshNotifications({ showToast = false } = {}) {
  if (!state.currentUser) {
    return;
  }

  const notifications = await api("/api/notifications");
  const newUnread = notifications.filter(
    (notification) => !notification.isRead && !state.knownNotificationIds.has(notification.id)
  );

  state.notifications = notifications;
  notifications.forEach((notification) => state.knownNotificationIds.add(notification.id));
  renderNotifications();

  if (showToast && newUnread.length > 0) {
    showNotificationToast(newUnread[0]);
  }
}

function startNotificationPolling() {
  stopNotificationPolling();
  notificationTimer = window.setInterval(() => {
    refreshNotifications({ showToast: true }).catch(() => {});
    if (state.currentUser?.role === "Admin" || state.currentUser?.role === "Agent") {
      refreshAbsences().catch(() => {});
    }
  }, 10000);
}

function stopNotificationPolling() {
  if (notificationTimer) {
    window.clearInterval(notificationTimer);
    notificationTimer = null;
  }
}

function updateStats(stats) {
  document.querySelector("#statTotal").textContent = stats.total;
  document.querySelector("#statOpen").textContent = stats.open;
  document.querySelector("#statInProgress").textContent = stats.inProgress;
  document.querySelector("#statReopened").textContent = stats.reopened;
  document.querySelector("#statClosed").textContent = stats.closed;
  document.querySelector("#statOverdue").textContent = stats.overdue;

  const averageRatingCard = document.querySelector("#averageRatingCard");
  const adminViewTabs = document.querySelector("#adminViewTabs");

  averageRatingCard.classList.toggle("hidden", state.currentUser.role !== "Agent");
  adminViewTabs.classList.toggle("hidden", state.currentUser.role === "User");

  if (state.currentUser.role === "Agent") {
    document.querySelector("#statAverageRating").textContent =
      stats.averageRating === null || stats.averageRating === undefined
        ? "-"
        : `${Number(stats.averageRating).toFixed(2)} (${stats.ratingCount})`;
  }

  if (state.currentUser.role === "Admin") {
    loadKpi().catch(() => {});
    if (state.adminView === "agents") {
      renderAgentList();
      renderAgentDetail();
    }
  }
}

//превключва между таб "Тикети" и таб "Агенти" (само за Admin)
function switchAdminView(view) {
  state.adminView = view;
  document.querySelector("#ticketsView").classList.toggle("hidden", view !== "tickets");
  document.querySelector("#agentsView").classList.toggle("hidden", view !== "agents");
  document.querySelector("#absencesView").classList.toggle("hidden", view !== "absences");

  document.querySelectorAll("#adminViewTabs .filter-button").forEach((button) => {
    button.classList.toggle("active", button.dataset.view === view);
  });

  if (view === "agents") {
    renderAgentList();
    renderAgentDetail();
  }

  if (view === "absences") {
    renderAbsenceList();
    renderAbsenceCalendar();
  }
}

//презарежда потребителите (напр. след добавяне/изтриване/редакция на агент) и опреснява зависимите изгледи
async function refreshUsers() {
  state.users = await api("/api/users");
  renderAdminTicketFilters();
  if (state.adminView === "agents") {
    renderAgentList();
    renderAgentDetail();
  }
}

//зарежда реалните KPI данни (не генерирани, директно изчислени от съществуващите тикети)
async function loadKpi() {
  const payload = await api("/api/kpi");
  state.agentKpis = payload.agents || [];
  if (state.adminView === "agents") {
    renderAgentDetail();
  }
}

function formatKpiNumber(value, digits = 2) {
  return value === null || value === undefined ? "-" : Number(value).toFixed(digits);
}

//лявата колона в таб "Агенти" - кликаем списък с агенти
function renderAgentList() {
  const container = document.querySelector("#agentList");
  const agents = state.users
    .filter((user) => user.role === "Agent")
    .slice()
    .sort((a, b) => a.fullName.localeCompare(b.fullName));

  if (agents.length === 0) {
    container.innerHTML = `<p class="empty">Няма агенти.</p>`;
    return;
  }

  container.innerHTML = agents
    .map(
      (agent) => `
        <button class="ticket-item ${agent.id === state.selectedAgentId ? "active" : ""}" data-agent-id="${agent.id}">
          <strong>${escapeHtml(agent.fullName)}</strong>
          <span class="ticket-meta">${(agent.specializations || []).map(escapeHtml).join(", ") || "Без специализация"}</span>
        </button>
      `
    )
    .join("");
}

//дясната колона в таб "Агенти" - KPI, специализации (редактируеми) и всички тикети на избрания агент
function renderAgentDetail() {
  const container = document.querySelector("#agentDetails");
  const agent = state.users.find((user) => user.id === state.selectedAgentId);

  if (!agent) {
    container.innerHTML = `<p class="empty">Избери агент, за да видиш подробности.</p>`;
    return;
  }

  const kpi = state.agentKpis.find((item) => item.agentId === agent.id);
  const agentTickets = state.tickets
    .filter((ticket) => ticket.assignedAgentId === agent.id)
    .slice()
    .sort((a, b) => new Date(b.updatedAtUtc) - new Date(a.updatedAtUtc));

  container.innerHTML = `
    <div class="detail-header">
      <div>
        <p class="eyebrow">Агент</p>
        <h2>${escapeHtml(agent.fullName)}</h2>
      </div>
    </div>

    <div class="detail-grid">
      <div class="detail-cell"><small>Решени тикети</small>${kpi ? kpi.totalResolved : 0}</div>
      <div class="detail-cell"><small>Активни в момента</small>${kpi ? kpi.activeTickets : 0}</div>
      <div class="detail-cell"><small>Средна оценка</small>${kpi ? formatKpiNumber(kpi.averageRating) : "-"}</div>
      <div class="detail-cell"><small>Средно време до затваряне</small>${
        kpi && kpi.averageResolutionMinutes !== null && kpi.averageResolutionMinutes !== undefined
          ? formatDuration(Math.round(kpi.averageResolutionMinutes))
          : "-"
      }</div>
      <div class="detail-cell"><small>Спазен SLA</small>${
        kpi && kpi.slaComplianceRate !== null && kpi.slaComplianceRate !== undefined
          ? `${formatKpiNumber(kpi.slaComplianceRate, 1)}%`
          : "-"
      }</div>
    </div>

    <section class="specialization-editor">
      <h3>Специализации</h3>
      <div class="specialization-checkboxes">
        ${AGENT_CATEGORIES.map(
          (category) => `
            <label class="specialization-option">
              <input type="checkbox" value="${escapeHtml(category)}" ${
                (agent.specializations || []).includes(category) ? "checked" : ""
              } />
              ${escapeHtml(category)}
            </label>
          `
        ).join("")}
      </div>
      <button type="button" id="saveSpecializationsButton" class="secondary">Запази специализации</button>
      <p id="saveSpecializationsMessage" class="hint"></p>
    </section>

    <section class="comments">
      <h3>Тикети на агента (${agentTickets.length})</h3>
      ${
        agentTickets.length
          ? `
            <div class="kpi-table-wrap">
              <table class="kpi-table">
                <thead>
                  <tr>
                    <th>Номер</th>
                    <th>Заглавие</th>
                    <th>Категория</th>
                    <th>Приоритет</th>
                    <th>Статус</th>
                    <th>Обновен</th>
                  </tr>
                </thead>
                <tbody>
                  ${agentTickets
                    .map(
                      (ticket) => `
                        <tr>
                          <td>${ticket.number}</td>
                          <td>${escapeHtml(ticket.title)}</td>
                          <td>${escapeHtml(ticket.category)}</td>
                          <td>${priorityLabels[ticket.priority] || ticket.priority}</td>
                          <td><span class="badge ${ticket.status}">${statusLabels[ticket.status]}</span></td>
                          <td>${formatDate(ticket.updatedAtUtc)}</td>
                        </tr>
                      `
                    )
                    .join("")}
                </tbody>
              </table>
            </div>
          `
          : `<p class="hint">Все още няма тикети, назначени на този агент.</p>`
      }
    </section>

    <div class="actions">
      <button type="button" id="deleteSelectedAgentButton" class="danger">Изтрий агент</button>
    </div>
  `;
}

function formatDateOnly(value) {
  const date = new Date(value);
  const day = String(date.getDate()).padStart(2, "0");
  const month = String(date.getMonth() + 1).padStart(2, "0");
  return `${day}/${month}/${date.getFullYear()}`;
}

function toDateKey(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

//зарежда отсъствията (Admin вижда всички, Agent - само своите), опреснява бадж-а на таба "Отпуски"
async function refreshAbsences() {
  if (!state.currentUser || (state.currentUser.role !== "Admin" && state.currentUser.role !== "Agent")) {
    return;
  }

  state.absences = await api("/api/absences");

  if (state.currentUser.role === "Agent") {
    state.myAbsences = await api("/api/absences/mine");
  }

  if (state.currentUser.role === "Admin") {
    const pendingCount = state.absences.filter((absence) => absence.status === "Pending").length;
    const badge = document.querySelector("#absencesTabBadge");
    badge.textContent = pendingCount;
    badge.classList.toggle("hidden", pendingCount === 0);
  }

  if (state.adminView === "absences") {
    renderAbsenceList();
    renderAbsenceCalendar();
  }
}

//рендира едно card за отсъствие - преизползва се и от "Моите отсъствия", и от отбора
//isOwn = картата е в секцията "Моите отсъствия" на текущия агент (не отборния списък)
function renderAbsenceCard(absence, isAdmin, isOwn = false) {
  const canCancel = isAdmin
    ? absence.status === "Approved"
    : isOwn && (absence.status === "Pending" || absence.status === "Approved");
  return `
    <article class="absence-card">
      <div class="absence-card-header">
        <strong>${escapeHtml(absence.agentName)}</strong>
        <span class="badge ${absence.status}">${absenceStatusLabels[absence.status] || absence.status}</span>
      </div>
      <span class="ticket-meta">
        <span class="badge">${absenceTypeLabels[absence.type] || absence.type}</span>
        <span>${formatDateOnly(absence.startDate)} – ${formatDateOnly(absence.endDate)}</span>
      </span>
      ${absence.reason ? `<p>${escapeHtml(absence.reason)}</p>` : ""}
      <span class="ticket-meta">Заявено от ${escapeHtml(absence.requestedByName)} · ${formatDate(absence.createdAtUtc)}</span>
      ${
        absence.decidedByName
          ? `<span class="ticket-meta">Решено от ${escapeHtml(absence.decidedByName)} · ${formatDate(absence.decidedAtUtc)}</span>`
          : ""
      }
      ${
        isAdmin && absence.status === "Pending"
          ? `
            <div class="actions">
              <button type="button" class="secondary" data-absence-action="approve" data-absence-id="${absence.id}">Одобри</button>
              <button type="button" class="danger" data-absence-action="reject" data-absence-id="${absence.id}">Отхвърли</button>
            </div>
          `
          : ""
      }
      ${
        canCancel
          ? `
            <div class="actions">
              <button type="button" class="secondary" data-absence-action="cancel" data-absence-id="${absence.id}">Анулирай</button>
            </div>
          `
          : ""
      }
    </article>
  `;
}

//разпределя собствените отсъствия на агента в чакащи/одобрени(предстоящи)/минали
function groupMyAbsences(absences) {
  const today = toDateKey(new Date());
  const groups = { pending: [], approved: [], past: [] };

  absences.forEach((absence) => {
    if (absence.status === "Pending") {
      groups.pending.push(absence);
    } else if (absence.status === "Approved" && absence.endDate >= today) {
      groups.approved.push(absence);
    } else {
      groups.past.push(absence);
    }
  });

  return groups;
}

function renderMyAbsencesGroup(title, absences) {
  if (absences.length === 0) {
    return "";
  }

  return `
    <div class="absence-group">
      <h4>${title} (${absences.length})</h4>
      ${absences.map((absence) => renderAbsenceCard(absence, false, true)).join("")}
    </div>
  `;
}

//таб "Отпуски" - за Agent: първо собствената история, групирана, после отпуските на екипа;
//за Admin: пълният списък с бутони за одобри/отхвърли/анулирай
function renderAbsenceList() {
  const container = document.querySelector("#absenceList");
  const isAdmin = state.currentUser.role === "Admin";
  const isAgent = state.currentUser.role === "Agent";
  const absences = state.absences.slice().sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc));

  let html = "";

  if (isAgent) {
    const grouped = groupMyAbsences(state.myAbsences);
    const myAbsencesHtml =
      renderMyAbsencesGroup("Чакащи одобрение", grouped.pending) +
      renderMyAbsencesGroup("Одобрени", grouped.approved) +
      renderMyAbsencesGroup("Минали", grouped.past);

    html += `
      <section class="my-absences-section">
        <h3>Моите отсъствия</h3>
        ${myAbsencesHtml || `<p class="hint">Все още нямаш подадени заявки.</p>`}
      </section>
      <h3 class="team-absences-heading">Отсъствия на екипа</h3>
    `;
  }

  html += absences.length === 0
    ? `<p class="empty">Няма подадени отсъствия.</p>`
    : absences.map((absence) => renderAbsenceCard(absence, isAdmin)).join("");

  container.innerHTML = html;
}

//месечен календар - кой агент кога е в одобрен отпуск
function renderAbsenceCalendar() {
  const grid = document.querySelector("#absenceCalendarGrid");
  const label = document.querySelector("#calendarMonthLabel");

  const year = state.calendarMonth.getFullYear();
  const month = state.calendarMonth.getMonth();

  label.textContent = new Intl.DateTimeFormat("bg-BG", { month: "long", year: "numeric" }).format(state.calendarMonth);

  const approvedAbsences = state.absences.filter((absence) => absence.status === "Approved");
  const firstOfMonth = new Date(year, month, 1);
  const startOffset = (firstOfMonth.getDay() + 6) % 7; //понеделник = 0
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const todayKey = toDateKey(new Date());

  const weekdayLabels = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Нд"];
  let html = weekdayLabels.map((day) => `<div class="calendar-weekday">${day}</div>`).join("");

  for (let i = 0; i < startOffset; i++) {
    html += `<div class="calendar-day outside-month"></div>`;
  }

  for (let day = 1; day <= daysInMonth; day++) {
    const dateKey = toDateKey(new Date(year, month, day));
    const agentsOnLeave = approvedAbsences.filter((absence) => dateKey >= absence.startDate && dateKey <= absence.endDate);

    html += `
      <div class="calendar-day ${dateKey === todayKey ? "today" : ""}">
        <div class="calendar-day-number">${day}</div>
        <div class="calendar-day-agents">
          ${agentsOnLeave
            .map(
              (absence) =>
                `<span class="calendar-agent-dot ${absence.type}" title="${escapeHtml(absence.agentName)} – ${absenceTypeLabels[absence.type]}">${escapeHtml(absence.agentName)}</span>`
            )
            .join("")}
        </div>
      </div>
    `;
  }

  grid.innerHTML = html;
}

function changeCalendarMonth(delta) {
  state.calendarMonth = new Date(state.calendarMonth.getFullYear(), state.calendarMonth.getMonth() + delta, 1);
  renderAbsenceCalendar();
}

function getVisibleTickets() {
  const search = state.search.toLowerCase();

  return state.tickets.filter((ticket) => {
    const matchesStatus = state.statusFilter === "All" || ticket.status === state.statusFilter;
    const matchesPriority = state.priorityFilter === "All" || ticket.priority === state.priorityFilter;
    const matchesCategory = state.categoryFilter === "All" || ticket.category === state.categoryFilter;
    const matchesAgent =
      state.currentUser.role !== "Admin" ||
      state.agentFilter === "All" ||
      ticket.assignedAgentId === Number(state.agentFilter);
    const matchesRequester =
      state.currentUser.role !== "Admin" ||
      state.requesterFilter === "All" ||
      ticket.requesterId === Number(state.requesterFilter);
    const matchesSearch =
      !search ||
      ticket.number.toLowerCase().includes(search) ||
      ticket.title.toLowerCase().includes(search) ||
      ticket.category.toLowerCase().includes(search);

    return matchesStatus && matchesPriority && matchesCategory && matchesAgent && matchesRequester && matchesSearch;
  });
}

function renderAdminTicketFilters() {
  if (state.currentUser.role !== "Admin") {
    return;
  }

  const agents = state.users.filter((user) => user.role === "Agent");
  const requesters = state.users.filter((user) => user.role === "User" || user.role === "Admin");

  document.querySelector("#agentTicketFilter").innerHTML = `
    <option value="All">Всички агенти</option>
    ${agents.map((agent) => `<option value="${agent.id}">${escapeHtml(agent.fullName)}</option>`).join("")}
  `;
  document.querySelector("#requesterTicketFilter").innerHTML = `
    <option value="All">Всички потребители</option>
    ${requesters.map((user) => `<option value="${user.id}">${escapeHtml(user.fullName)}</option>`).join("")}
  `;
}

function renderTicketList() {
  const tickets = getVisibleTickets();
  const activeTickets = tickets.filter((ticket) => activeStatuses.has(ticket.status));
  const inactiveTickets = tickets.filter((ticket) => !activeStatuses.has(ticket.status));

  if (tickets.length === 0) {
    ticketList.innerHTML = `<p class="empty">Няма тикети по избраните критерии.</p>`;
    return;
  }

  ticketList.innerHTML = `
    ${renderTicketGroup("Активни тикети", activeTickets)}
    ${renderTicketGroup("Неактивни тикети", inactiveTickets)}
  `;
}

function renderTicketGroup(title, tickets) {
  return `
    <section class="ticket-group">
      <h3>${title}</h3>
      ${
        tickets.length
          ? tickets.map(renderTicketItem).join("")
          : `<p class="group-empty">Няма тикети в тази категория.</p>`
      }
    </section>
  `;
}

function renderTicketItem(ticket) {
  const slaText = formatRemaining(ticket.remainingMinutes, ticket.isOverdue);

  return `
    <button class="ticket-item ${ticket.id === state.selectedTicketId ? "active" : ""}" data-ticket-id="${ticket.id}">
      <span class="ticket-meta">${ticket.number} · ${formatDate(ticket.updatedAtUtc)}</span>
      <strong>${ticket.title}</strong>
      <span class="tag-row">
        <span class="badge ${ticket.status}">${statusLabels[ticket.status]}</span>
        <span class="badge ${ticket.priority}">${priorityLabels[ticket.priority]}</span>
        <span class="badge ${ticket.isOverdue ? "overdue" : ""}">SLA: ${slaText}</span>
        ${ticket.status === "Closed" ? `<span class="badge work-time">Работа: ${formatDuration(ticket.workDurationMinutes)}</span>` : ""}
      </span>
      <span class="ticket-meta">Агент: ${ticket.agentName}</span>
    </button>
  `;
}

function renderTicketDetails() {
  const ticket = state.tickets.find((item) => item.id === state.selectedTicketId);

  if (!ticket) {
    ticketDetails.innerHTML = `<p class="empty">Избери тикет, за да видиш подробности и действия.</p>`;
    return;
  }

  const isAdmin = state.currentUser.role === "Admin";
  const isRequester = ticket.requesterId === state.currentUser.id;
  const canReopen = isRequester && ticket.status === "Closed";
  const canGiveFeedback = isRequester && ticket.status === "Closed" && !ticket.feedback;
  const canWork = isAdmin || (state.currentUser.role === "Agent" && ticket.assignedAgentId === state.currentUser.id);
  const agents = state.users.filter((user) => user.role === "Agent");

  ticketDetails.innerHTML = `
    <div class="detail-header">
      <div>
        <p class="eyebrow">${ticket.number}</p>
        <h2>${ticket.title}</h2>
        <span class="tag-row">
          <span class="badge ${ticket.status}">${statusLabels[ticket.status]}</span>
          <span class="badge ${ticket.priority}">${priorityLabels[ticket.priority]}</span>
          <span class="badge ${ticket.isOverdue ? "overdue" : ""}">
            SLA: ${formatRemaining(ticket.remainingMinutes, ticket.isOverdue)}
          </span>
        </span>
      </div>
      <small>Обновен: ${formatDate(ticket.updatedAtUtc)}</small>
    </div>

    <p>${escapeHtml(ticket.description)}</p>

    <div class="detail-grid">
      <div class="detail-cell"><small>Заявител</small>${ticket.requesterName}</div>
      <div class="detail-cell"><small>Назначен агент</small>${ticket.agentName}</div>
      <div class="detail-cell"><small>Категория</small>${ticket.category}</div>
      <div class="detail-cell"><small>Подкатегория</small>${ticket.subCategory || "—"}</div>
      <div class="detail-cell"><small>Краен срок</small>${formatDate(ticket.dueAtUtc)}</div>
      <div class="detail-cell"><small>Започнат</small>${ticket.startedAtUtc ? formatDate(ticket.startedAtUtc) : "Не е започнат"}</div>
      <div class="detail-cell"><small>Време на работа</small>${ticket.status === "Closed" ? formatDuration(ticket.workDurationMinutes) : "Тикетът не е затворен"}</div>
      <div class="detail-cell"><small>Оценка</small>${renderFeedbackSummary(ticket.feedback)}</div>
    </div>

    <div class="actions">
      ${canWork && ticket.status !== "Closed" && ticket.status !== "InProgress" ? `<button id="startTicketButton">Маркирай в прогрес</button>` : ""}
      ${canWork && ticket.status === "InProgress" ? `<button id="closeTicketButton" class="danger">Затвори тикета</button>` : ""}
      ${canReopen ? `<button id="reopenTicketButton">Отвори отново</button>` : ""}
      ${canGiveFeedback ? `<button id="openFeedbackButton" class="secondary">Дай обратна връзка</button>` : ""}
      ${isAdmin ? `<button id="deleteTicketButton" class="danger">Изтрий тикета</button>` : ""}
    </div>

    ${
      isAdmin
        ? `
          <div class="reassign-box">
            <select id="agentSelect">
              ${agents
                .map((agent) => `<option value="${agent.id}" ${ticket.assignedAgentId === agent.id ? "selected" : ""}>${agent.fullName}</option>`)
                .join("")}
            </select>
            <button id="reassignButton" class="secondary">Преразпредели</button>
          </div>
        `
        : ""
    }

    <section class="attachments">
      <h3>Прикачени файлове</h3>
      ${
        ticket.attachments.length
          ? ticket.attachments
              .map(
                (attachment) => `
                  <div class="attachment-item">
                    <a href="/api/tickets/${ticket.id}/attachments/${attachment.id}" target="_blank" rel="noopener">${escapeHtml(attachment.fileName)}</a>
                    <span class="attachment-meta">${formatFileSize(attachment.sizeBytes)} · ${escapeHtml(attachment.uploadedByName)} · ${formatDate(attachment.uploadedAtUtc)}</span>
                    ${
                      attachment.uploadedByUserId === state.currentUser.id || isAdmin
                        ? `<button type="button" class="secondary" data-delete-attachment-id="${attachment.id}">Изтрий</button>`
                        : ""
                    }
                  </div>
                `
              )
              .join("")
          : `<p class="hint">Няма прикачени файлове.</p>`
      }
      ${
        ticket.status !== "Closed"
          ? `
            <form id="attachmentUploadForm" class="comment-form">
              <label>
                Прикачи файл (макс. 3MB - jpg, png, webp, gif, pdf, docx, xlsx, txt, log)
                <input type="file" name="file" required accept=".jpg,.jpeg,.png,.webp,.gif,.pdf,.docx,.xlsx,.txt,.log" />
              </label>
              <button type="submit">Качи файл</button>
              <p id="attachmentUploadError" class="error"></p>
            </form>
          `
          : `<p class="hint">Тикетът е затворен – прикачените файлове остават запазени за справка, но не могат да се качват нови.</p>`
      }
    </section>

    <section class="comments">
      <h3>Коментари</h3>
      ${
        ticket.comments.length
          ? ticket.comments
              .map(
                (comment) => `
                  <div class="comment">
                    <strong>${comment.authorName} · ${formatDate(comment.createdAtUtc)}</strong>
                    <p>${escapeHtml(comment.text)}</p>
                  </div>
                `
              )
              .join("")
          : `<p class="hint">Все още няма коментари по тикета.</p>`
      }
    </section>

    <form id="commentForm" class="comment-form">
      <label>
        Нов коментар
        <textarea name="text" rows="3" required></textarea>
      </label>
      <button type="submit">Добави коментар</button>
    </form>
  `;
}

function renderFeedbackSummary(feedback) {
  if (!feedback) {
    return "Няма оценка";
  }

  return `
    <span class="rating-summary">${"★".repeat(feedback.rating)}${"☆".repeat(5 - feedback.rating)}</span>
    ${feedback.comment ? `<p class="feedback-comment">${escapeHtml(feedback.comment)}</p>` : ""}
  `;
}

function renderNotifications() {
  const unreadCount = state.notifications.filter((notification) => !notification.isRead).length;
  notificationButton.classList.toggle("has-unread", unreadCount > 0);
  markAllNotificationsReadButton.disabled = unreadCount === 0;
  document.querySelector("#notificationCount").textContent = unreadCount;
  document.querySelector("#notificationUnreadText").textContent =
    unreadCount === 1 ? "1 непрочетена" : `${unreadCount} непрочетени`;

  if (state.notifications.length === 0) {
    notificationList.innerHTML = `<p class="notification-empty">Няма нотификации.</p>`;
    return;
  }

  notificationList.innerHTML = state.notifications
    .map(
      (notification) => `
        <button class="notification-item ${notification.isRead ? "" : "unread"}" data-notification-id="${notification.id}" data-ticket-id="${notification.ticketId}">
          <strong>${escapeHtml(notification.message)}</strong>
          ${notification.type === "FeedbackRequest" ? `<em>Изисква оценка</em>` : ""}
          ${notification.type === "DeletedTicket" ? `<em>Изтрит тикет</em>` : ""}
          <span>${formatDate(notification.createdAtUtc)}</span>
        </button>
      `
    )
    .join("");
}

function showLatestUnreadNotification() {
  const notification = state.notifications.find((item) => !item.isRead);
  if (notification) {
    showNotificationToast(notification);
  }
}

function showNotificationToast(notification) {
  activeNotificationToast?.remove();

  const toast = document.createElement("button");
  toast.type = "button";
  toast.className = "notification-toast";
  toast.dataset.notificationId = notification.id;
  toast.dataset.ticketId = notification.ticketId;
  toast.dataset.notificationType = notification.type || "OpenTicket";
  toast.innerHTML = `
    <strong>${escapeHtml(notification.message)}</strong>
    <span>${getNotificationActionLabel(notification.type)}</span>
  `;

  document.body.append(toast);
  activeNotificationToast = toast;
  window.setTimeout(() => toast.remove(), 6500);
}

function getNotificationActionLabel(type) {
  if (type === "FeedbackRequest") {
    return "Остави оценка";
  }

  if (type === "DeletedTicket") {
    return "Виж нотификацията";
  }

  return "Отвори тикета";
}

//коментари
function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  loginError.textContent = "";

  try {
    const payload = await api("/api/login", {
      method: "POST",
      body: JSON.stringify(Object.fromEntries(new FormData(loginForm)))
    });

    state.currentUser = payload;
    showApp();
    await loadDashboard();
  } catch (error) {
    loginError.textContent = error.message;
  }
});

document.querySelector("#logoutButton").addEventListener("click", async () => {
  await api("/api/logout", { method: "POST" });
  state.currentUser = null;
  state.selectedTicketId = null;
  state.feedbackTicketId = null;
  state.notifications = [];
  state.knownNotificationIds = new Set();
  activeNotificationToast?.remove();
  activeNotificationToast = null;
  showLogin();
});

const createCategorySelect = document.querySelector("#createCategorySelect");
const createSubCategorySelect = document.querySelector("#createSubCategorySelect");
const createAttachmentInput = document.querySelector("#createAttachmentInput");
const createTicketError = document.querySelector("#createTicketError");
const categorySuggestionsEl = document.querySelector("#categorySuggestions");

//зарежда таксономията категория -> подкатегории веднъж и я кешира в state
async function ensureCategoryTaxonomy() {
  if (!state.categoryTaxonomy) {
    state.categoryTaxonomy = await api("/api/tickets/categories");
  }
  return state.categoryTaxonomy;
}

//попълва падащото меню за подкатегория спрямо избраната категория (каскадно меню)
function populateSubCategoryOptions(topCategory, preferredSubCategory) {
  const subCategories = (state.categoryTaxonomy && state.categoryTaxonomy[topCategory]) || [];
  createSubCategorySelect.innerHTML = subCategories
    .map((sub) => `<option value="${escapeHtml(sub)}">${escapeHtml(sub)}</option>`)
    .join("");

  if (preferredSubCategory && subCategories.includes(preferredSubCategory)) {
    createSubCategorySelect.value = preferredSubCategory;
  }
}

createCategorySelect.addEventListener("change", () => {
  populateSubCategoryOptions(createCategorySelect.value);
});

openCreateDialogButton.addEventListener("click", async () => {
  if (state.currentUser.role === "Agent") {
    return;
  }

  createTicketForm.reset();
  createTicketError.textContent = "";
  hideCategorySuggestions();

  await ensureCategoryTaxonomy();
  populateSubCategoryOptions(createCategorySelect.value);

  createDialog.showModal();
});

document.querySelector("#cancelCreateButton").addEventListener("click", () => {
  createDialog.close();
});

createTicketForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  createTicketError.textContent = "";

  const form = new FormData(createTicketForm);
  const file = createAttachmentInput.files[0];

  let ticket;
  try {
    ticket = await api("/api/tickets", {
      method: "POST",
      body: JSON.stringify({
        title: form.get("title"),
        description: form.get("description"),
        category: form.get("category"),
        subCategory: form.get("subCategory"),
        priority: form.get("priority")
      })
    });
  } catch (error) {
    createTicketError.textContent = error.message;
    return;
  }

  if (file) {
    try {
      await uploadAttachment(ticket.id, file);
    } catch (error) {
      createTicketError.textContent = `Тикетът е създаден, но файлът не бе качен: ${error.message}`;
    }
  }

  state.selectedTicketId = ticket.id;
  createDialog.close();
  await refreshTickets();
  await refreshNotifications();
});

function hideCategorySuggestions() {
  categorySuggestionsEl.innerHTML = "";
  categorySuggestionsEl.classList.add("hidden");
}

//предложенията се показват като падащо меню под полето за заглавие; всеки ред включва категория и подкатегория
function renderCategorySuggestions(label, items) {
  if (!items || items.length === 0) {
    hideCategorySuggestions();
    return;
  }

  categorySuggestionsEl.innerHTML = `
    <span class="suggestion-label">${label}</span>
    ${items
      .map(
        (item) => `
          <button type="button" class="suggestion-option" data-category="${escapeHtml(item.topCategory)}" data-subcategory="${escapeHtml(item.subCategory)}">
            <span class="suggestion-option-category">${escapeHtml(item.topCategory)}</span>
            <span class="suggestion-option-subcategory">${escapeHtml(item.subCategory)}</span>
          </button>
        `
      )
      .join("")}
  `;
  categorySuggestionsEl.classList.remove("hidden");
}

//предложения по последните категории тикети на потребителя - показват се при фокус на празно поле
async function showRecentCategorySuggestions() {
  try {
    const categories = await api("/api/tickets/recent-categories");
    renderCategorySuggestions("Скорошни категории", categories);
  } catch {
    hideCategorySuggestions();
  }
}

//предложения по ключови думи в заглавието - сменят предложението, щом потребителят започне да пише
async function showKeywordCategorySuggestions(title) {
  try {
    const suggestions = await api(`/api/tickets/suggest-category?title=${encodeURIComponent(title)}`);
    renderCategorySuggestions("Предложения по заглавието", suggestions);
  } catch {
    hideCategorySuggestions();
  }
}

let categorySuggestionTimer = null;

const createTicketTitleInput = createTicketForm.querySelector('input[name="title"]');

createTicketTitleInput.addEventListener("focus", () => {
  if (createTicketTitleInput.value.trim().length === 0) {
    showRecentCategorySuggestions();
  }
});

createTicketTitleInput.addEventListener("input", () => {
  window.clearTimeout(categorySuggestionTimer);
  const title = createTicketTitleInput.value.trim();

  categorySuggestionTimer = window.setTimeout(() => {
    if (title.length === 0) {
      showRecentCategorySuggestions();
    } else if (title.length >= 3) {
      showKeywordCategorySuggestions(title);
    } else {
      hideCategorySuggestions();
    }
  }, 400);
});

categorySuggestionsEl.addEventListener("click", (event) => {
  const option = event.target.closest("[data-category]");
  if (!option) {
    return;
  }

  createCategorySelect.value = option.dataset.category;
  populateSubCategoryOptions(option.dataset.category, option.dataset.subcategory);
  hideCategorySuggestions();
});

document.addEventListener("click", (event) => {
  if (event.target.closest(".title-field")) {
    return;
  }
  hideCategorySuggestions();
});

ticketList.addEventListener("click", (event) => {
  const item = event.target.closest("[data-ticket-id]");
  if (!item) {
    return;
  }

  state.selectedTicketId = Number(item.dataset.ticketId);
  renderTicketList();
  renderTicketDetails();
});

ticketDetails.addEventListener("click", async (event) => {
  if (!state.selectedTicketId) {
    return;
  }

  const button = event.target.closest("button");
  if (!button) {
    return;
  }

  if (button.id === "closeTicketButton") {
    await api(`/api/tickets/${state.selectedTicketId}/close`, { method: "POST" });
    await refreshTickets();
    await refreshNotifications();
  }

  if (button.id === "startTicketButton") {
    await api(`/api/tickets/${state.selectedTicketId}/start`, { method: "POST" });
    await refreshTickets();
    await refreshNotifications();
  }

  if (button.id === "reopenTicketButton") {
    await api(`/api/tickets/${state.selectedTicketId}/reopen`, { method: "POST" });
    await refreshTickets();
    await refreshNotifications();
  }

  if (button.id === "reassignButton") {
    const agentId = Number(document.querySelector("#agentSelect").value);
    const ticket = await api(`/api/tickets/${state.selectedTicketId}/reassign`, {
      method: "POST",
      body: JSON.stringify({ agentId })
    });
    await refreshTickets();
    await refreshNotifications();
    reassignConfirmText.textContent = `Тикет ${ticket.number} е преразпределен към ${ticket.agentName}.`;
    reassignConfirmDialog.showModal();
  }

  if (button.id === "deleteTicketButton") {
    const confirmed = window.confirm("Сигурни ли сте, че искате да изтриете този тикет?");
    if (!confirmed) {
      return;
    }

    await api(`/api/tickets/${state.selectedTicketId}/delete`, { method: "POST" });
    state.selectedTicketId = null;
    await refreshTickets();
    await refreshNotifications();
  }

  if (button.id === "openFeedbackButton") {
    openFeedbackDialog(state.selectedTicketId);
  }

  const deleteAttachmentButton = button.closest("[data-delete-attachment-id]");
  if (deleteAttachmentButton) {
    const confirmed = window.confirm("Сигурни ли сте, че искате да изтриете този файл?");
    if (!confirmed) {
      return;
    }

    try {
      await api(`/api/tickets/${state.selectedTicketId}/attachments/${deleteAttachmentButton.dataset.deleteAttachmentId}`, {
        method: "DELETE"
      });
      await refreshTickets();
    } catch (error) {
      window.alert(error.message);
    }
  }
});

//качва прикачен файл - multipart/form-data, затова не минава през общата api() помощна функция
async function uploadAttachment(ticketId, file) {
  const formData = new FormData();
  formData.append("file", file);

  const response = await fetch(`/api/tickets/${ticketId}/attachments`, {
    method: "POST",
    body: formData
  });

  const payload = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(payload.message || "Възникна грешка при качването.");
  }

  return payload;
}

ticketDetails.addEventListener("submit", async (event) => {
  if (event.target.id === "attachmentUploadForm") {
    event.preventDefault();

    const fileInput = event.target.querySelector('input[name="file"]');
    const errorEl = document.querySelector("#attachmentUploadError");
    errorEl.textContent = "";

    const file = fileInput.files[0];
    if (!file) {
      return;
    }

    try {
      await uploadAttachment(state.selectedTicketId, file);
      await refreshTickets();
    } catch (error) {
      errorEl.textContent = error.message;
    }

    return;
  }

  if (event.target.id !== "commentForm") {
    return;
  }

  event.preventDefault();

  await api(`/api/tickets/${state.selectedTicketId}/comments`, {
    method: "POST",
    body: JSON.stringify(Object.fromEntries(new FormData(event.target)))
  });

  await refreshTickets();
  await refreshNotifications();
});

document.querySelector("#exportKpiButton").addEventListener("click", () => {
  window.location.href = "/api/kpi/export";
});

document.querySelector("#agentList").addEventListener("click", (event) => {
  const button = event.target.closest("[data-agent-id]");
  if (!button) {
    return;
  }

  state.selectedAgentId = Number(button.dataset.agentId);
  renderAgentList();
  renderAgentDetail();
});

document.querySelector("#agentDetails").addEventListener("click", async (event) => {
  const button = event.target.closest("button");
  if (!button || !state.selectedAgentId) {
    return;
  }

  if (button.id === "saveSpecializationsButton") {
    const checked = Array.from(
      document.querySelectorAll("#agentDetails .specialization-checkboxes input:checked")
    ).map((input) => input.value);

    const message = document.querySelector("#saveSpecializationsMessage");
    try {
      await api(`/api/agents/${state.selectedAgentId}/specializations`, {
        method: "PUT",
        body: JSON.stringify({ specializations: checked })
      });
      message.textContent = "Запазено.";
      await refreshUsers();
    } catch (error) {
      message.textContent = error.message;
    }
  }

  if (button.id === "deleteSelectedAgentButton") {
    const confirmed = window.confirm("Сигурни ли сте, че искате да изтриете този агент?");
    if (!confirmed) {
      return;
    }

    try {
      await api(`/api/agents/${state.selectedAgentId}`, { method: "DELETE" });
      state.selectedAgentId = null;
      await refreshUsers();
    } catch (error) {
      window.alert(error.message);
    }
  }
});

document.querySelector("#openCreateAgentDialogButton").addEventListener("click", () => {
  document.querySelector("#createAgentForm").reset();
  document.querySelector("#createAgentError").textContent = "";
  document.querySelector("#createAgentDialog").showModal();
});

document.querySelector("#cancelCreateAgentButton").addEventListener("click", () => {
  document.querySelector("#createAgentDialog").close();
});

document.querySelector("#createAgentForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  document.querySelector("#createAgentError").textContent = "";

  try {
    const form = Object.fromEntries(new FormData(event.target));
    const agent = await api("/api/agents", {
      method: "POST",
      body: JSON.stringify(form)
    });

    document.querySelector("#createAgentDialog").close();
    state.selectedAgentId = agent.id;
    await refreshUsers();
  } catch (error) {
    document.querySelector("#createAgentError").textContent = error.message;
  }
});

document.querySelector("#absenceList").addEventListener("click", async (event) => {
  const button = event.target.closest("[data-absence-action]");
  if (!button) {
    return;
  }

  const id = Number(button.dataset.absenceId);
  const action = button.dataset.absenceAction;

  try {
    if (action === "approve") {
      await api(`/api/absences/${id}/decide`, { method: "POST", body: JSON.stringify({ approve: true }) });
    } else if (action === "reject") {
      await api(`/api/absences/${id}/decide`, { method: "POST", body: JSON.stringify({ approve: false }) });
    } else if (action === "cancel") {
      if (!window.confirm("Сигурни ли сте, че искате да анулирате тази отпуска?")) {
        return;
      }
      await api(`/api/absences/${id}/cancel`, { method: "POST" });
    }
    await refreshAbsences();
  } catch (error) {
    window.alert(error.message);
  }
});

document.querySelector("#calendarPrevButton").addEventListener("click", () => changeCalendarMonth(-1));
document.querySelector("#calendarNextButton").addEventListener("click", () => changeCalendarMonth(1));

document.querySelector("#openCreateAbsenceDialogButton").addEventListener("click", () => {
  const agents = state.users.filter((user) => user.role === "Agent");
  document.querySelector("#absenceAgentSelect").innerHTML = agents
    .map((agent) => `<option value="${agent.id}">${escapeHtml(agent.fullName)}</option>`)
    .join("");

  document.querySelector("#createAbsenceForm").reset();
  document.querySelector("#createAbsenceError").textContent = "";
  document.querySelector("#createAbsenceDialog").showModal();
});

document.querySelector("#cancelCreateAbsenceButton").addEventListener("click", () => {
  document.querySelector("#createAbsenceDialog").close();
});

document.querySelector("#createAbsenceForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  document.querySelector("#createAbsenceError").textContent = "";

  try {
    const form = Object.fromEntries(new FormData(event.target));
    await api("/api/absences/admin", {
      method: "POST",
      body: JSON.stringify({
        agentId: Number(form.agentId),
        startDate: form.startDate,
        endDate: form.endDate,
        type: form.type,
        reason: form.reason
      })
    });

    document.querySelector("#createAbsenceDialog").close();
    await refreshAbsences();
  } catch (error) {
    document.querySelector("#createAbsenceError").textContent = error.message;
  }
});

document.querySelector("#requestAbsenceButton").addEventListener("click", () => {
  document.querySelector("#requestAbsenceForm").reset();
  document.querySelector("#requestAbsenceError").textContent = "";
  document.querySelector("#requestAbsenceDialog").showModal();
});

document.querySelector("#cancelRequestAbsenceButton").addEventListener("click", () => {
  document.querySelector("#requestAbsenceDialog").close();
});

document.querySelector("#requestAbsenceForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  document.querySelector("#requestAbsenceError").textContent = "";

  try {
    const form = Object.fromEntries(new FormData(event.target));
    await api("/api/absences", {
      method: "POST",
      body: JSON.stringify({
        startDate: form.startDate,
        endDate: form.endDate,
        type: form.type,
        reason: form.reason
      })
    });

    document.querySelector("#requestAbsenceDialog").close();
    window.alert("Заявката е изпратена и очаква одобрение от администратор.");
  } catch (error) {
    document.querySelector("#requestAbsenceError").textContent = error.message;
  }
});

notificationButton.addEventListener("click", () => {
  const isHidden = notificationPanel.classList.toggle("hidden");
  notificationButton.setAttribute("aria-expanded", String(!isHidden));
});

markAllNotificationsReadButton.addEventListener("click", async () => {
  await api("/api/notifications/read-all", { method: "POST" });
  await refreshNotifications();
});

document.addEventListener("click", (event) => {
  if (event.target.closest(".notification-center")) {
    return;
  }

  notificationPanel.classList.add("hidden");
  notificationButton.setAttribute("aria-expanded", "false");
});

function openFeedbackDialog(ticketId) {
  const ticket = state.tickets.find((item) => item.id === ticketId);
  if (!ticket || ticket.feedback) {
    return;
  }

  state.feedbackTicketId = ticketId;
  feedbackForm.reset();
  document.querySelector("#feedbackError").textContent = "";
  document.querySelector("#feedbackTicketTitle").textContent = `${ticket.number} · ${ticket.title}`;
  feedbackDialog.showModal();
}

async function openNotification(notificationId, ticketId, type = "OpenTicket") {
  await api(`/api/notifications/${notificationId}/read`, { method: "POST" });
  await Promise.all([refreshTickets(), refreshNotifications()]);
  state.selectedTicketId = ticketId;
  renderTicketList();
  renderTicketDetails();
  notificationPanel.classList.add("hidden");
  notificationButton.setAttribute("aria-expanded", "false");

  if (type === "DeletedTicket") {
    state.selectedTicketId = null;
    renderTicketList();
    ticketDetails.innerHTML = `<p class="empty">Този тикет е изтрит от администратор.</p>`;
    return;
  }

  if (type === "FeedbackRequest") {
    openFeedbackDialog(ticketId);
    return;
  }

  document.querySelector("#ticketDetails").scrollIntoView({ behavior: "smooth", block: "start" });
}

notificationList.addEventListener("click", async (event) => {
  const item = event.target.closest("[data-notification-id]");
  if (!item) {
    return;
  }

  const notification = state.notifications.find((entry) => entry.id === Number(item.dataset.notificationId));
  await openNotification(Number(item.dataset.notificationId), Number(item.dataset.ticketId), notification?.type);
});

document.body.addEventListener("click", async (event) => {
  const toast = event.target.closest(".notification-toast");
  if (!toast) {
    return;
  }

  await openNotification(
    Number(toast.dataset.notificationId),
    Number(toast.dataset.ticketId),
    toast.dataset.notificationType
  );
  toast.remove();
});

document.querySelector("#cancelFeedbackButton").addEventListener("click", () => {
  feedbackDialog.close();
});

document.querySelector("#closeReassignConfirmButton").addEventListener("click", () => {
  reassignConfirmDialog.close();
});

feedbackForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  document.querySelector("#feedbackError").textContent = "";

  try {
    const form = Object.fromEntries(new FormData(feedbackForm));
    await api(`/api/tickets/${state.feedbackTicketId}/feedback`, {
      method: "POST",
      body: JSON.stringify({
        rating: Number(form.rating),
        comment: form.comment || ""
      })
    });

    feedbackDialog.close();
    state.feedbackTicketId = null;
    await refreshTickets();
    await refreshNotifications();
  } catch (error) {
    document.querySelector("#feedbackError").textContent = error.message;
  }
});

document.querySelector("#searchInput").addEventListener("input", (event) => {
  state.search = event.target.value;
  renderTicketList();
});

document.querySelector("#priorityFilter").addEventListener("change", (event) => {
  state.priorityFilter = event.target.value;
  renderTicketList();
});

document.querySelector("#categoryFilter").addEventListener("change", (event) => {
  state.categoryFilter = event.target.value;
  renderTicketList();
});

document.querySelector("#agentTicketFilter").addEventListener("change", (event) => {
  state.agentFilter = event.target.value;
  state.selectedTicketId = null;
  renderTicketList();
  renderTicketDetails();
});

document.querySelector("#requesterTicketFilter").addEventListener("change", (event) => {
  state.requesterFilter = event.target.value;
  state.selectedTicketId = null;
  renderTicketList();
  renderTicketDetails();
});

document.querySelector("#clearFiltersButton").addEventListener("click", () => {
  state.search = "";
  state.statusFilter = "All";
  state.priorityFilter = "All";
  state.categoryFilter = "All";
  state.agentFilter = "All";
  state.requesterFilter = "All";
  state.selectedTicketId = null;

  document.querySelector("#searchInput").value = "";
  document.querySelector("#statusFilter").value = "All";
  document.querySelector("#priorityFilter").value = "All";
  document.querySelector("#categoryFilter").value = "All";
  document.querySelector("#agentTicketFilter").value = "All";
  document.querySelector("#requesterTicketFilter").value = "All";

  renderTicketList();
  renderTicketDetails();
});

document.querySelector("#statusFilter").addEventListener("change", (event) => {
  state.statusFilter = event.target.value;
  renderTicketList();
});

document.querySelector("#filtersMenuButton").addEventListener("click", () => {
  const filtersMenuButton = document.querySelector("#filtersMenuButton");
  const panel = document.querySelector("#filtersMenuPanel");
  const willOpen = panel.classList.contains("hidden");
  panel.classList.toggle("hidden");

  if (willOpen) {
    const buttonRect = filtersMenuButton.getBoundingClientRect();
    const top = buttonRect.bottom + 6;
    panel.style.top = `${top}px`;
    panel.style.left = `${buttonRect.right - panel.offsetWidth}px`;
    //ограничава височината до реално наличното място под бутона, с вътрешен скрол ако не стига
    panel.style.maxHeight = `${Math.max(160, window.innerHeight - top - 12)}px`;
  }
});

document.addEventListener("click", (event) => {
  if (event.target.closest(".filters-menu")) {
    return;
  }
  document.querySelector("#filtersMenuPanel").classList.add("hidden");
});

document.querySelectorAll("#adminViewTabs .filter-button").forEach((button) => {
  button.addEventListener("click", () => {
    switchAdminView(button.dataset.view);
  });
});

async function boot() {
  try {
    state.currentUser = await api("/api/me");
    showApp();
    await loadDashboard();
  } catch {
    showLogin();
  }
}

boot();
