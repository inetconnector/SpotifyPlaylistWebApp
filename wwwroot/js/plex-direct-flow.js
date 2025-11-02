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

    updateStatus(status, plexStrings.statusLoadingTrackUrls || "⏳ Loading track URLs…");

    try {
        // Load URLs
        const res = await fetch(`/PlexDirect/Urls?playlistKey=${encodeURIComponent(playlistKey)}`);
        if (!res.ok) return updateStatus(status, plexStrings.statusLoadUrlsError || "❌ Failed to load URLs.", true);

        const data = await res.json();
        const items = data.items;
        if (!items || !items.length) return updateStatus(status, plexStrings.statusNoItems || "⚠️ No tracks found.", true);

        const loadingFilesTemplate = plexStrings.statusLoadingFiles || "📥 Downloading {0} files…";
        updateStatus(status, loadingFilesTemplate.replace("{0}", items.length));

        // Create ZIP
        const zip = new JSZip();

        let index = 0;
        for (const item of items) {
            index++;
            const progressTemplate = plexStrings.statusLoadingFileProgress || "📥 Downloading {0}/{1}: {2}";
            updateStatus(status, progressTemplate
                .replace("{0}", index)
                .replace("{1}", items.length)
                .replace("{2}", item.filename));

            const fileRes = await fetch(item.url);
            const blob = await fileRes.blob();
            zip.file(item.filename, blob);
        }

        updateStatus(status, plexStrings.statusCreatingZip || "📦 Creating ZIP…");

        const zipBlob = await zip.generateAsync({ type: "blob" });
        const url = URL.createObjectURL(zipBlob);

        const a = document.createElement("a");
        a.href = url;
        a.download = "playlist.zip";
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);

        updateStatus(status, plexStrings.statusDownloadComplete || "✅ ZIP downloaded!");

    } catch (err) {
        console.error("ZIP failed:", err);
        updateStatus(status, plexStrings.statusDownloadFailed || "❌ Download failed.", true);
    }
};


// Helper
// -------------------------------------------------------------
function updateStatus(elem, msg, isError = false) {
    if (!elem) return;
    elem.style.color = isError ? "#ff7070" : "#ffffff";
    elem.textContent = msg;
}
