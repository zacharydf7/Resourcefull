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

// What the dashboard may change, as the app mapped it; nothing until the features endpoint answers.
let features = { changes: false, downloadLinks: false, uploadLinks: false };

// Sends a change. The header tells the server the change came from this page, since another site's page can't send it.
async function send(method, path, parameters, body) {
  const query = new URLSearchParams();
  for (const [name, value] of Object.entries(parameters || {})) {
    if (value !== null && value !== undefined) query.set(name, value);
  }
  const headers = { "X-Resourcefull-Dashboard": "1", Accept: "application/json" };
  let content = body;
  // A file goes as it is, with its own type; anything else as JSON.
  if (body !== undefined && !(body instanceof Blob)) {
    headers["Content-Type"] = "application/json";
    content = JSON.stringify(body);
  }
  const response = await fetch("api/" + path + (query.size ? "?" + query : ""), { method, headers, body: content });
  if (response.status === 404) throw new Error("This no longer exists.");
  const json = (response.headers.get("Content-Type") || "").includes("json") ? await response.json() : null;
  if (!response.ok) {
    throw new Error(json && json.title ? json.title : response.status === 401 || response.status === 403
      ? "You aren't allowed to change this."
      : "The change failed (" + response.status + ").");
  }
  return json;
}

// Runs a change, saying in the status what it's doing and then what it did or why it failed. The result isn't shown if
// stillShown says the view the change started from was left meanwhile, since the status then belongs to another view.
async function attempt(status, work, stillShown = () => true) {
  status.textContent = "Working…";
  status.classList.remove("error");
  try {
    const message = await work();
    if (stillShown()) status.textContent = message;
    return true;
  } catch (error) {
    if (stillShown()) showError(status, error);
    return false;
  }
}

// A form's field by name. form.elements.namedItem avoids clashes between field names and the form's own members, such
// as name or reset.
function field(form, name) {
  return form.elements.namedItem(name);
}

function actionButton(text, action, className) {
  const button = element("button", text, className);
  button.type = "button";
  button.addEventListener("click", action);
  return button;
}

// A time as a datetime-local input takes it, in the browser's time zone.
function toLocalInput(value) {
  if (!value) return "";
  const date = new Date(value);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
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

// The container the change forms were last shown for, so reloading it after a change keeps what the change said.
let formsContainer = null;

async function showContainer(name, focus) {
  shownContainer = name;
  shownFile = null;
  showView("container-view");
  const heading = document.getElementById("container-name");
  heading.textContent = name;
  if (focus) heading.focus();
  document.title = name + " · Resourcefull";
  const summary = document.getElementById("container-summary");
  summary.replaceChildren();
  // Switching containers clears what the forms showed for the last one, such as an upload link.
  if (name !== formsContainer) {
    formsContainer = name;
    for (const form of [uploadForm, uploadLinkForm]) {
      form.reset();
      form.querySelector(".status").textContent = "";
    }
    field(uploadLinkForm, "url").hidden = true;
    document.getElementById("delete-container-status").textContent = "";
  }
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

let shownFile = null;

function showVersions(container, name, focus) {
  shownContainer = null;
  // A change's status message survives reloading the same file; the download link and the edit form don't.
  if (!isShown({ container, name })) {
    document.getElementById("versions-change").textContent = "";
    document.getElementById("versions-change").classList.remove("error");
  }
  shownFile = { container, name };
  showView("versions-view");
  document.getElementById("edit-form").hidden = true;
  document.getElementById("download-link").hidden = true;
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
    if (features.changes) tr.append(versionActions(version));
    return tr;
  }, "No versions.");
}

function versionActions(version) {
  const cell = element("td", null, "actions");
  if (!version.isLatest) cell.append(actionButton("Restore", () => restoreVersion(version)));
  cell.append(actionButton("Edit", () => editVersion(version)));
  if (features.downloadLinks) cell.append(actionButton("Link", () => downloadLink(version)));
  cell.append(actionButton("Delete", () => deleteVersion(version), "danger"));
  return cell;
}

function fileParameters(versionId) {
  return { container: shownFile.container, name: shownFile.name, versionId };
}

// Whether the versions view shows this file, even if it was left and opened again since.
function isShown(file) {
  return shownFile !== null && shownFile.container === file.container && shownFile.name === file.name;
}

// Shows the file's versions again after a change to them, unless another view is shown.
function reloadVersions(file) {
  if (isShown(file)) showVersions(file.container, file.name, false);
}

async function restoreVersion(version) {
  const file = shownFile;
  const status = document.getElementById("versions-change");
  if (await attempt(status, async () => {
    await send("POST", "restore", fileParameters(version.versionId));
    return "Restored version " + version.versionId + " as a new current version.";
  }, () => isShown(file))) reloadVersions(file);
}

async function deleteVersion(version) {
  if (!confirm("Delete version " + version.versionId + "?")) return;
  const file = shownFile;
  const status = document.getElementById("versions-change");
  if (await attempt(status, async () => {
    await send("DELETE", "resource", fileParameters(version.versionId));
    return "Deleted version " + version.versionId + ".";
  }, () => isShown(file))) reloadVersions(file);
}

async function downloadLink(version) {
  const file = shownFile;
  const output = document.getElementById("download-link");
  output.hidden = true;
  await attempt(document.getElementById("versions-change"), async () => {
    const link = await send("POST", "download-link", fileParameters(version.versionId), { expiresInMinutes: 60 });
    if (isShown(file)) {
      output.value = link.url;
      output.hidden = false;
      output.select();
    }
    return "Anyone with this link can download version " + version.versionId + " until " + formatDate(link.expiresAt) + ".";
  }, () => isShown(file));
}

let editing = null;

function editVersion(version) {
  editing = version;
  const form = document.getElementById("edit-form");
  document.getElementById("edit-heading").textContent = "Edit version " + version.versionId;
  field(form, "contentType").value = version.contentType || "";
  field(form, "expiresAt").value = toLocalInput(version.expiresAt);
  field(form, "metadata").value = Object.entries(version.metadata).map(([key, value]) => key + "=" + value).join("\n");
  form.querySelector(".status").textContent = "";
  form.hidden = false;
  field(form, "contentType").focus();
}

function parseMetadata(text) {
  const metadata = {};
  for (const line of text.split("\n").map(l => l.trim()).filter(l => l)) {
    const equals = line.indexOf("=");
    if (equals <= 0) throw new Error("Each line of metadata needs a key, then =, then its value.");
    metadata[line.slice(0, equals).trim()] = line.slice(equals + 1).trim();
  }
  return metadata;
}

document.getElementById("edit-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.target;
  const version = editing;
  const file = shownFile;
  const parameters = fileParameters(version.versionId);
  if (await attempt(form.querySelector(".status"), async () => {
    const metadata = parseMetadata(field(form, "metadata").value);
    const contentType = field(form, "contentType").value.trim() || null;
    const expires = field(form, "expiresAt").value;
    // Only what changed is sent, since each is a write of its own, and a container may refuse one of them.
    if (contentType !== (version.contentType || null)) await send("PUT", "content-type", parameters, { contentType });
    if (expires !== toLocalInput(version.expiresAt)) {
      await send("PUT", "expiry", parameters, { expiresAt: expires ? new Date(expires).toISOString() : null });
    }
    if (JSON.stringify(metadata) !== JSON.stringify(version.metadata)) await send("PUT", "metadata", parameters, { metadata });
    return "Saved.";
  }, () => isShown(file)) && isShown(file)) {
    // Reloading the versions closes the form, so the confirmation goes in the status above the table.
    document.getElementById("versions-change").textContent = "Saved changes to version " + version.versionId + ".";
    reloadVersions(file);
  }
});

document.getElementById("edit-cancel").addEventListener("click", () => {
  document.getElementById("edit-form").hidden = true;
});

document.getElementById("delete-file").addEventListener("click", async () => {
  const file = shownFile;
  const { container, name } = file;
  if (!confirm("Delete every version of " + name + "?")) return;
  if (await attempt(document.getElementById("versions-change"), async () => {
    await send("DELETE", "resource", { container, name });
    return "Deleted " + name + ".";
  }, () => isShown(file)) && isShown(file)) {
    location.hash = "#container=" + encodeURIComponent(container);
  }
});

function showContainers(focus) {
  shownContainer = null;
  shownFile = null;
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

const createForm = document.getElementById("create-container");
createForm.addEventListener("submit", async event => {
  event.preventDefault();
  const name = field(createForm, "name").value;
  if (await attempt(createForm.querySelector(".status"), async () => {
    await send("POST", "containers", null, { name });
    return "Created " + name + ".";
  })) {
    field(createForm, "name").value = "";
    containers.reload();
  }
});

const uploadForm = document.getElementById("upload-form");
// The name starts as the file's, until it's changed by hand.
let suggestedName = "";
field(uploadForm, "file").addEventListener("change", () => {
  const file = field(uploadForm, "file").files[0];
  const name = field(uploadForm, "name");
  if (file && (name.value === "" || name.value === suggestedName)) name.value = suggestedName = file.name;
});
uploadForm.addEventListener("submit", async event => {
  event.preventDefault();
  const container = shownContainer;
  const name = field(uploadForm, "name").value;
  if (await attempt(uploadForm.querySelector(".status"), async () => {
    await send("PUT", "upload", { container, name, replace: field(uploadForm, "replace").checked ? "true" : null },
      field(uploadForm, "file").files[0]);
    return "Uploaded " + name + ".";
  }, () => shownContainer === container) && shownContainer === container) {
    // Only on the container uploaded to: another one's form may hold a file picked since.
    uploadForm.reset();
    suggestedName = "";
    showContainer(container, false);
  }
});

const uploadLinkForm = document.getElementById("upload-link-form");
uploadLinkForm.addEventListener("submit", async event => {
  event.preventDefault();
  const output = field(uploadLinkForm, "url");
  output.hidden = true;
  const container = shownContainer;
  const name = field(uploadLinkForm, "name").value;
  const prefix = field(uploadLinkForm, "prefix").checked;
  await attempt(uploadLinkForm.querySelector(".status"), async () => {
    const link = await send("POST", "upload-link", { container }, {
      name: prefix ? null : name,
      namePrefix: prefix ? name : null,
      expiresInMinutes: Number(field(uploadLinkForm, "minutes").value),
      replace: field(uploadLinkForm, "replace").checked
    });
    if (shownContainer === container) {
      output.value = link.url;
      output.hidden = false;
      output.select();
    }
    return "Anyone with this link can upload until " + formatDate(link.expiresAt) + ".";
  }, () => shownContainer === container);
});

document.getElementById("delete-container").addEventListener("click", async () => {
  const name = shownContainer;
  const typed = prompt("This deletes every file in " + name + ". Type the container's name to delete it.");
  if (typed === null) return;
  const status = document.getElementById("delete-container-status");
  if (typed !== name) {
    showError(status, new Error("The name didn't match, so nothing was deleted."));
    return;
  }
  if (await attempt(status, async () => {
    await send("DELETE", "container", { name });
    return "Deleted " + name + ".";
  }, () => shownContainer === name)) {
    if (shownContainer === name) location.hash = "#";
    containers.reload();
  }
});

// Shows the forms and buttons for the changes the app allows. Until the features endpoint answers, the dashboard only
// reads.
async function loadFeatures() {
  try {
    features = await api("features");
  } catch {
    return;
  }
  for (const node of document.querySelectorAll("[data-change]")) node.hidden = !features.changes;
  for (const node of document.querySelectorAll("[data-upload-links]")) node.hidden = !features.uploadLinks;
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
// The views show their change buttons only if they know the changes are allowed.
loadFeatures().then(() => route(false));
