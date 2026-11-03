"use strict";

// The Resourcefull dashboard: reads the JSON endpoints next to this file and shows them. Every value from the store is
// put on the page as text, never as markup, since container and file names can hold anything.

const units = ["B", "KB", "MB", "GB", "TB"];

function formatSize(bytes) {
  if (bytes === null || bytes === undefined) return "";
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return (unit === 0 || value >= 100 ? Math.round(value) : value.toFixed(1)) + " " + units[unit];
}

function formatDate(value) {
  return value ? new Date(value).toLocaleString() : "";
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = String(text);
  if (className) node.className = className;
  return node;
}

async function api(path, parameters, signal) {
  const query = new URLSearchParams();
  for (const [name, value] of Object.entries(parameters || {})) {
    if (value) query.set(name, value);
  }
  const response = await fetch("api/" + path + (query.size ? "?" + query : ""),
    { headers: { Accept: "application/json" }, signal });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(response.status === 401 || response.status === 403
    ? "You aren't allowed to see this."
    : "The dashboard couldn't load this (" + response.status + ").");
  return response.json();
}

function ofLimit(used, limit, format) {
  return limit === null ? format(used) : format(used) + " of " + format(limit);
}

// A cell showing how much of a limit is used: the amount, and a meter when there's a limit.
function usageCell(used, limit, format) {
  const cell = element("td");
  cell.append(element("span", ofLimit(used, limit, format)));
  if (limit !== null && limit > 0) {
    const meter = element("meter");
    meter.min = 0;
    meter.max = limit;
    meter.value = Math.min(used, limit);
    meter.high = limit * 0.9;
    meter.optimum = 0;
    meter.title = Math.round(used / limit * 100) + "% used";
    cell.append(meter);
  }
  return cell;
}

function limitsText(policy) {
  const limits = [];
  if (policy.readOnly) limits.push("read-only");
  if (policy.maxResourceSize !== null) limits.push("files up to " + formatSize(policy.maxResourceSize));
  if (policy.allowedContentTypes) limits.push("only " + policy.allowedContentTypes.join(", "));
  if (policy.maxVersions !== null) limits.push(policy.maxVersions === 1 ? "no older versions kept" : policy.maxVersions + " versions kept");
  return limits.join("; ");
}

// A link to a file's versions; both names are encoded, so either can hold "&" or "=".
function fileLink(container, name) {
  const link = element("a", name);
  link.href = "#container=" + encodeURIComponent(container) + "&file=" + encodeURIComponent(name);
  return link;
}

function nameCell(container, name) {
  const cell = element("th");
  cell.scope = "row";
  cell.append(fileLink(container, name));
  return cell;
}

function versionText(version) {
  return version.isLatest ? "current" : "older";
}

function showError(status, error) {
  status.textContent = error.message;
  status.classList.add("error");
}

// A table that loads a page at a time, with a filter and a "Show more" button.
function pagedTable({ body, status, more, filter, load, row, empty }) {
  let token = null;
  let prefix = "";
  let generation = 0;
  let request = new AbortController();
  let typing;

  async function next(reset) {
    const current = reset ? ++generation : generation;
    if (reset) {
      // A request for the old filter would still run every query on the server, so it's canceled.
      request.abort();
      request = new AbortController();
      token = null;
      body.replaceChildren();
    }
    status.textContent = "Loading…";
    status.classList.remove("error");
    more.hidden = true;
    try {
      const page = await load(prefix, token, request.signal);
      if (current !== generation) return;
      if (page === null) {
        status.textContent = "This no longer exists.";
        return;
      }
      body.append(...page.items.map(row));
      token = page.continuationToken;
      more.hidden = !token;
      status.textContent = body.childElementCount === 0 ? empty : "";
    } catch (error) {
      if (current === generation && error.name !== "AbortError") showError(status, error);
    }
  }

  more.addEventListener("click", () => next(false));
  filter.addEventListener("submit", event => event.preventDefault());
  filter.prefix.addEventListener("input", () => {
    clearTimeout(typing);
    typing = setTimeout(() => {
      prefix = filter.prefix.value;
      next(true);
    }, 300);
  });
  return {
    reload() {
      prefix = filter.prefix.value;
      return next(true);
    }
  };
}

const containers = pagedTable({
  body: document.getElementById("containers"),
  status: document.getElementById("containers-status"),
  more: document.getElementById("containers-more"),
  filter: document.getElementById("containers-filter"),
  load: (prefix, continuationToken, signal) => api("containers", { prefix, continuationToken }, signal),
  empty: "No containers.",
  row(container) {
    const tr = element("tr");
    const name = element("th");
    name.scope = "row";
    const link = element("a", container.name);
    link.href = "#container=" + encodeURIComponent(container.name);
    name.append(link);
    tr.append(name,
      usageCell(container.usage.storedSize, container.policy.maxContainerSize, formatSize),
      usageCell(container.usage.resourceCount, container.policy.maxResourceCount, n => n.toLocaleString()),
      element("td", container.usage.versionCount.toLocaleString(), "number"),
      element("td", limitsText(container.policy)));
    return tr;
  }
});

let shownContainer = null;
// Goes up each time a view is shown. A response that arrives after its view was left, or after the same view was
// opened again, is dropped, so it isn't shown in the wrong view or shown twice.
let viewRequest = 0;
// Cancels the current view's lists when another view is shown: the server reads every version in a container for the
// largest and expiring lists, so a list no one will see shouldn't keep it busy.
let viewLoads = new AbortController();

const views = ["containers-view", "container-view", "versions-view"];

function showView(id) {
  for (const view of views) document.getElementById(view).hidden = view !== id;
  viewLoads.abort();
  viewLoads = new AbortController();
}

const resources = pagedTable({
  body: document.getElementById("resources"),
  status: document.getElementById("resources-status"),
  more: document.getElementById("resources-more"),
  filter: document.getElementById("resources-filter"),
  load: (prefix, continuationToken, signal) => api("resources", { container: shownContainer, prefix, continuationToken }, signal),
  empty: "No files.",
  row(resource) {
    const tr = element("tr");
    tr.append(nameCell(shownContainer, resource.name),
      element("td", formatSize(resource.contentLength), "number"),
      element("td", resource.contentType),
      element("td", formatDate(resource.lastModified)),
      element("td", formatDate(resource.expiresAt)));
    return tr;
  }
});

function summaryItem(list, term, description) {
  list.append(element("dt", term), element("dd", description));
}

// Fills a table from an endpoint that answers a single list, unless another view was shown meanwhile.
async function fillList(id, path, parameters, current, row, empty) {
  const body = document.getElementById(id);
  const status = document.getElementById(id + "-status");
  body.replaceChildren();
  status.textContent = "Loading…";
  status.classList.remove("error");
  try {
    const list = await api(path, parameters, viewLoads.signal);
    if (current !== viewRequest) return;
    if (list === null) {
      status.textContent = "This no longer exists.";
      return;
    }
    body.append(...list.items.map(row));
    status.textContent = list.items.length === 0 ? empty : "";
  } catch (error) {
    if (current === viewRequest && error.name !== "AbortError") showError(status, error);
  }
}

async function showContainer(name, focus) {
  shownContainer = name;
  showView("container-view");
  const heading = document.getElementById("container-name");
  heading.textContent = name;
  if (focus) heading.focus();
  document.title = name + " · Resourcefull";
  const summary = document.getElementById("container-summary");
  summary.replaceChildren();
  document.getElementById("resources-filter").prefix.value = "";
  resources.reload();
  const current = ++viewRequest;
  fillList("largest", "largest", { container: name }, current, version => {
    const tr = element("tr");
    tr.append(nameCell(name, version.name), element("td", formatSize(version.contentLength), "number"),
      element("td", versionText(version)));
    return tr;
  }, "No files.");
  fillList("expiring", "expiring", { container: name }, current, version => {
    const tr = element("tr");
    tr.append(nameCell(name, version.name), element("td", formatDate(version.expiresAt)), element("td", versionText(version)));
    return tr;
  }, "Nothing expires.");
  try {
    const container = await api("container", { name });
    if (current !== viewRequest) return;
    if (container === null) {
      summaryItem(summary, "Container", "This container no longer exists.");
      return;
    }
    const { usage, policy } = container;
    summaryItem(summary, "Stored", ofLimit(usage.storedSize, policy.maxContainerSize, formatSize));
    summaryItem(summary, "Files", ofLimit(usage.resourceCount, policy.maxResourceCount, n => n.toLocaleString()));
    summaryItem(summary, "Versions", usage.versionCount.toLocaleString());
    summaryItem(summary, "Limits", limitsText(policy) || "none");
    for (const [key, value] of Object.entries(container.metadata)) summaryItem(summary, key, value);
  } catch (error) {
    if (current === viewRequest) summaryItem(summary, "Container", error.message);
  }
}

function showVersions(container, name, focus) {
  shownContainer = null;
  showView("versions-view");
  document.getElementById("versions-container").textContent = container;
  document.getElementById("versions-back").href = "#container=" + encodeURIComponent(container);
  const heading = document.getElementById("versions-name");
  heading.textContent = name;
  if (focus) heading.focus();
  document.title = name + " · " + container + " · Resourcefull";
  fillList("versions", "versions", { container, name }, ++viewRequest, version => {
    const tr = element("tr");
    const id = element("th", version.versionId + " (" + versionText(version) + ")");
    id.scope = "row";
    tr.append(id,
      element("td", formatSize(version.contentLength), "number"),
      element("td", version.contentType),
      element("td", formatDate(version.lastModified)),
      element("td", formatDate(version.expiresAt)));
    return tr;
  }, "No versions.");
}

function showContainers(focus) {
  shownContainer = null;
  viewRequest++;
  showView("containers-view");
  document.title = "Resourcefull";
  if (focus) document.getElementById("containers-heading").focus();
}

async function showHealth() {
  const health = document.getElementById("health");
  try {
    const report = await api("health");
    if (report.status === null) {
      health.textContent = "No health checks registered.";
      return;
    }
    health.replaceChildren(element("span", report.status, "badge " + report.status.toLowerCase()));
    for (const check of report.checks) {
      if (check.description) health.append(" ", element("span", check.description));
    }
  } catch (error) {
    showError(health, error);
  }
}

// How many expired versions the sweep hasn't deleted yet, store-wide. A provider without expiry shows nothing.
async function showExpiry() {
  const expiry = document.getElementById("expiry");
  try {
    const expired = await api("expired");
    if (!expired.supported) return;
    expiry.hidden = false;
    if (expired.count === 0) {
      expiry.textContent = "No expired versions are waiting for the sweep.";
      return;
    }
    const count = expired.more ? "More than " + expired.count.toLocaleString() : expired.count.toLocaleString();
    expiry.textContent = count + (expired.count === 1 && !expired.more ? " expired version is" : " expired versions are") +
      " waiting for the sweep; the oldest expired " + formatDate(expired.oldestExpiry) + ".";
  } catch (error) {
    expiry.hidden = false;
    showError(expiry, error);
  }
}

function route(focus) {
  const parameters = new URLSearchParams(location.hash.slice(1));
  const container = parameters.get("container");
  const file = parameters.get("file");
  if (container !== null && file !== null) showVersions(container, file, focus);
  else if (container !== null) showContainer(container, focus);
  else showContainers(focus);
}

window.addEventListener("hashchange", () => route(true));
showHealth();
showExpiry();
containers.reload();
route(false);
