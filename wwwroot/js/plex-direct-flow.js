// wwwroot/js/plex-direct-flow.js
// PLEX DIRECT DOWNLOAD – Client-Side ZIP Creation
// -------------------------------------------------------------

const plexStrings = window.PlexDirectStrings || {};

// Starts Plex Direct Download Flow (login if needed)
window.startPlexDirectDownloadFlow = window.startPlexDirectDownloadFlow || (async function () {
    try {
        await fetch('/Plex/SetIntent', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ intent: 'directDownload' })
        });
    } catch { }

    try { localStorage.setItem('plex.intent', 'directDownload'); } catch { }

    try {
        const chk = await fetch('/Plex/IsLoggedIn', { cache: 'no-store' });
        if (chk.ok) {
            const j = await chk.json();
            if (j && j.ok) {
                window.location.href = '/PlexDirect/DirectDownload';
                return;
            }
        }
    } catch { }

    if (typeof window.startPlexLogin === 'function') {
        window.startPlexLogin('directDownload');
    } else {
        window.location.href = '/PlexDirect/DirectDownload';
    }
});


// Load Playlists into Dropdown
// -------------------------------------------------------------
window.loadPlexPlaylists = async function ({ selectId = "playlistSelect", metaId = "playlistMeta" } = {}) {
    const ddl = document.getElementById(selectId);
    const meta = document.getElementById(metaId);

    if (!ddl) return console.error("playlistSelect not found");

    ddl.innerHTML = `<option>${plexStrings.loadPlaylistsOption || "Load playlists…"}</option>`;

    try {
        const res = await fetch("/HomePlex/GetPlexPlaylists");
        const data = await res.json();

        if (!data.success) {
            ddl.innerHTML = `<option>❌ ${data.message}</option>`;
            return;
        }

        ddl.innerHTML = "";
        data.playlists.forEach(p => {
            const opt = document.createElement("option");
            opt.value = p.ratingKey;
            opt.textContent = p.title;
            ddl.appendChild(opt);
        });

        if (meta) {
            const metaTemplate = plexStrings.metaPlaylistsFound || "{0} playlists found";
            meta.textContent = metaTemplate.replace("{0}", data.playlists.length);
            meta.classList.remove("hidden");
        }

    } catch (err) {
        ddl.innerHTML = `<option>${plexStrings.loadPlaylistsError || "❌ Failed to load"}</option>`;
        console.error(err);
    }
};


// Client-Side ZIP Download
// -------------------------------------------------------------
window.startPlexDirectZip = async function ({
    selectId = "playlistSelect",
    statusId = "statusArea"
} = {}) {
    const ddl = document.getElementById(selectId);
    const status = document.getElementById(statusId);
    if (!ddl) return;

    const playlistKey = ddl.value;
    if (!playlistKey) return updateStatus(status, plexStrings.statusSelectPlaylist || "⚠️ Please choose a playlist.", true);

    const optionIndex = ddl.selectedIndex >= 0 ? ddl.selectedIndex : 0;
    const selectedOption = ddl.options[optionIndex];
    const playlistNameRaw = selectedOption ? selectedOption.textContent || "" : "";
    const playlistNameSanitized = sanitizeFileName((playlistNameRaw || "playlist").trim()) || "playlist";

    updateStatus(status, plexStrings.statusCreatingZip || "📦 Creating ZIP…");

    // ein Request → Browser lädt direkt die ZIP-Datei
    const url = `/PlexDirect/Zip?playlistKey=${encodeURIComponent(playlistKey)}&playlistName=${encodeURIComponent(playlistNameSanitized)}`;

    // Navigieren startet den Download (Content-Disposition: attachment)
    window.location.href = url;

    // optional: nach ein paar Sekunden neutralen Status setzen
    setTimeout(() => {
        updateStatus(status, plexStrings.statusDownloadStarted || "✅ Download started…");
    }, 1500);
};



// Helper
// -------------------------------------------------------------
function updateStatus(elem, msg, isError = false) {
    if (!elem) return;
    elem.style.color = isError ? "#ff7070" : "#ffffff";
    elem.textContent = msg;
}

function sanitizeFileName(name) {
    if (!name) return "";
    return name
        .replace(/[\\/:*?"<>|]/g, "_")
        .replace(/\s+/g, " ")
        .trim()
        .replace(/^\.+$/, "")
        .substring(0, 120);
}
