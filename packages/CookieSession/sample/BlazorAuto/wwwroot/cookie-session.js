// Browser half of the cookie companion.
// Refresh tokens stay in the HttpOnly cookie the browser stores by itself.
// This file keeps the latest access token in a module variable only. It does not
// touch browser web storage. Web Locks serialize tabs in this browser. Without
// navigator.locks, only this tab is serialized; other tabs then depend on GoTrue's
// refresh-token reuse window, which is a mitigation, not a proof.
// A BroadcastChannel carries only { type: "signed-out" }. It never carries a token.
(function () {
  let memory = null;
  let queue = Promise.resolve();
  let signOutRef = null;
  const channel = typeof BroadcastChannel === "function" ? new BroadcastChannel("sb-cookie-session-signal") : null;

  function notifySignedOut() {
    memory = null;
    if (channel) channel.postMessage({ type: "signed-out" });
  }

  if (channel) {
    channel.onmessage = function (event) {
      var data = event.data;
      if (!data || data.type !== "signed-out") return;
      memory = null;
      if (signOutRef) signOutRef.invokeMethodAsync("OnRemoteSignOut");
    };
  }

  function csrf() {
    const meta = document.querySelector('meta[name="csrf-token"]');
    return meta ? meta.content : "";
  }

  function publicConfig() {
    const node = document.getElementById("cookie-session-public");
    if (!node || !node.textContent) return { url: "", anonKey: "" };
    return JSON.parse(node.textContent);
  }

  function withLock(work) {
    if (navigator.locks && navigator.locks.request) {
      return navigator.locks.request("sb-cookie-session", work);
    }
    const run = queue.catch(function () { return undefined; }).then(work);
    queue = run;
    return run;
  }

  function stillFresh(force) {
    if (force || !memory || !memory.expires_at) return false;
    return Date.parse(memory.expires_at) - Date.now() > 60000;
  }

  async function readToken(response) {
    const text = await response.text();
    let body = null;
    if (text) {
      try { body = JSON.parse(text); } catch { body = null; }
    }
    if (body && Object.prototype.hasOwnProperty.call(body, "refresh_token")) {
      memory = null;
      return { ok: false, status: 500, error: "refresh token was returned to the browser" };
    }
    if (response.status === 401) {
      memory = null;
      return { ok: false, status: 401, error: "reauth" };
    }
    if (!response.ok || !body || !body.access_token) {
      return { ok: false, status: response.status, error: "token request failed" };
    }
    memory = {
      access_token: body.access_token,
      expires_at: body.expires_at,
      expires_in: body.expires_in,
      user: body.user
    };
    return { ok: true, access_token: body.access_token, expires_at: body.expires_at, expires_in: body.expires_in, user: body.user };
  }

  async function post(path, payload, accessToken) {
    const headers = {
      "Content-Type": "application/json",
      "RequestVerificationToken": csrf()
    };
    if (accessToken) headers.Authorization = "Bearer " + accessToken;
    const response = await fetch(path, {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      headers: headers,
      body: payload === undefined ? "{}" : JSON.stringify(payload)
    });
    return readToken(response);
  }

  window.cookieSession = {
    publicConfig: publicConfig,
    acquire: function (force) {
      return withLock(async function () {
        if (stillFresh(force)) {
          return { ok: true, access_token: memory.access_token, expires_at: memory.expires_at, expires_in: memory.expires_in, user: memory.user };
        }
        let result = await post("/auth/session/token");
        if (result.status === 409) result = await post("/auth/session/token");
        return result;
      });
    },
    signIn: function (email, password) {
      return withLock(function () {
        memory = null;
        return post("/auth/session/signin", { email: email, password: password });
      });
    },
    notifySignedOut: notifySignedOut,
    watchSignOut: function (dotNetRef) { signOutRef = dotNetRef; },
    signOut: function (accessToken) {
      return withLock(async function () {
        memory = null;
        await post("/auth/session/signout", {}, accessToken);
        notifySignedOut();
        return { ok: true, status: 204 };
      });
    }
  };
})();
