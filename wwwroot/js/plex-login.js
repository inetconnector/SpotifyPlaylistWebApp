 /**
    * Starts a complete Plex login flow on the client side.
    * Everything (PIN creation, polling, and redirect) happens in the browser.
    * The server is contacted only after token retrieval.
    */
    async function startPlexLogin() {
    try {
        // === Step 1: Request new PIN directly from Plex ===
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
    console.log("[Plex] PIN created:", pin);

    // === Step 2: Open Plex login window ===
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

    // === Step 3: Poll Plex for token (user-side, same IP) ===
    const token = await pollPlexToken(pin.id, pin.clientIdentifier);

    if (!token) {
        alert("⚠️ Plex login timeout or cancelled.");
    if (loginWin && !loginWin.closed) loginWin.close();
    return;
        }

    console.log("[Plex] Token received:", token);

    // === Step 4: Save token to your server ===
    await fetch("/Home/SavePlexToken", {
        method: "POST",
    headers: {"Content-Type": "application/json" },
    body: JSON.stringify({token})
        });

    if (loginWin && !loginWin.closed) loginWin.close();
    window.location.href = "/Home/PlexActions";
    } catch (err) {
        console.error("Plex login failed:", err);
    alert("Unexpected error during Plex login. Check console for details.");
    }
}

    /**
     * Polls Plex.tv for the auth token associated with the given PIN.
     */
    async function pollPlexToken(pinId, clientId) {
    for (let attempt = 0; attempt < 30; attempt++) {
        try {
            const url = `https://plex.tv/api/v2/pins/${pinId}?includeClient=true&X-Plex-Client-Identifier=${clientId}`;
    const res = await fetch(url, {headers: {Accept: "application/json" } });
    const json = await res.json();

    if (json.authToken) return json.authToken;
            await new Promise(r => setTimeout(r, 2000)); // wait 2 s between polls
        } catch (err) {
        console.warn("Polling error:", err);
            await new Promise(r => setTimeout(r, 2000));
        }
    }
    return null;
} 
