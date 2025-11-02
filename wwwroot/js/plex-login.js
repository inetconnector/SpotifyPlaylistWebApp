const INTENT_LS_KEY = "plex.intent";

async function setIntent(intent) {
  try {
    await fetch("/Plex/SetIntent", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ intent })
    });
  } catch {}
  try { localStorage.setItem(INTENT_LS_KEY, intent); } catch {}
}

async function consumeNextUrl() {
  try {
    const r = await fetch("/Plex/NextUrl", { cache: "no-store" });
    if (r.ok) {
      const j = await r.json();
      if (j && j.url) return j.url;
    }
  } catch {}
  let url = "/Home/PlexActions";
  try {
    const intent = localStorage.getItem(INTENT_LS_KEY) || "plexActions";
    if (intent === "directDownload") url = "/PlexDirect/DirectDownload";
  } catch {}
  return url;
}

async function startPlexLogin(intent) {
  try {
    if (intent) await setIntent(intent);

    const res = await fetch("https://plex.tv/api/v2/pins?strong=true", {
      method: "POST",
      headers: {
        "Accept": "application/json",
        "X-Plex-Product": "SpotifyToPlex",
        "X-Plex-Device": "Browser",
        "X-Plex-Platform": "WebApp",
        "X-Plex-Version": "1.0",
        "X-Plex-Client-Identifier": "inetconnector-spotify-to-plex"
      }
    });
    if (!res.ok) throw new Error("Failed to create Plex PIN");
    const pin = await res.json();

    const loginUrl = `https://app.plex.tv/auth#?clientID=${pin.clientIdentifier}` +
      `&code=${pin.code}` +
      `&context[device][product]=SpotifyToPlex` +
      `&context[device][device]=Browser` +
      `&context[device][platform]=WebApp`;

    const loginWin = window.open(
      loginUrl,
      "PlexLogin",
      "width=750,height=700,menubar=no,toolbar=no,location=no,resizable=yes,scrollbars=yes"
    );

    const token = await pollPlexToken(pin.id, pin.clientIdentifier);
    if (!token) {
      alert("⚠️ Plex login timeout or cancelled.");
      if (loginWin && !loginWin.closed) loginWin.close();
      return;
    }

    await fetch("/Home/SavePlexToken", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token })
    });

    if (loginWin && !loginWin.closed) loginWin.close();

    const nextUrl = await consumeNextUrl();
    fetch("/Plex/ClearIntent", { method: "POST" }).catch(()=>{});
    try { localStorage.removeItem(INTENT_LS_KEY); } catch {}
    window.location.href = nextUrl;

  } catch (err) {
    console.error("Plex login failed:", err);
    alert("Unexpected error during Plex login. Check console for details.");
  }
}

async function pollPlexToken(pinId, clientId) {
  for (let attempt = 0; attempt < 30; attempt++) {
    try {
      const url = `https://plex.tv/api/v2/pins/${pinId}?includeClient=true&X-Plex-Client-Identifier=${clientId}`;
      const res = await fetch(url, { headers: { Accept: "application/json" } });
      const json = await res.json();
      if (json.authToken) return json.authToken;
      await new Promise(r => setTimeout(r, 2000));
    } catch {
      await new Promise(r => setTimeout(r, 2000));
    }
  }
  return null;
}

window.startPlexLogin = startPlexLogin;
window.setPlexIntent = setIntent;
y

async function startFlowA() {
  try {
    // kick off pin flow using existing helpers
    const pin = await (typeof createPlexPin !== 'undefined' ? createPlexPin() : null);
    if (!pin || !pin.id || !pin.clientIdentifier) { alert('Plex PIN konnte nicht erstellt werden.'); return; }
    const plexUrl = `https://app.plex.tv/auth#?clientID=${encodeURIComponent(pin.clientIdentifier)}&forwardUrl=https%3A%2F%2Fapp.plex.tv%2Fdesktop%23%21%2Flogin%3FpinID%3D${encodeURIComponent(pin.id)}&context%5Bdevice%5D%5Bproduct%5D=InetConnector%20Playlist%20Generator`;
    window.open(plexUrl, "_blank", "noopener,noreferrer,width=980,height=860");
    const token = await (typeof pollForAuthToken !== 'undefined' ? pollForAuthToken(pin.id, pin.clientIdentifier) : null);
    if (!token) { alert('Plex Login nicht abgeschlossen.'); return; }
    try {
      await fetch('/Plex/SaveToken', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token }) });
    } catch {}
    window.location.href = '/Home/Login';
  } catch (e) { console.error(e); alert('Unerwarteter Fehler im Plex→Spotify-Flow.'); }
}
window.startFlowA = startFlowA;
