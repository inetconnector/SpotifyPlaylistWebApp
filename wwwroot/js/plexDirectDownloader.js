import { SimpleZipWriter } from '/js/simpleZipWriter.js';

const playlistKey = window.__PLEX_PLAYLIST_KEY__ || '';
const playlistName = window.__PLEX_PLAYLIST_NAME__ || playlistKey;
const startBtn = document.getElementById('startBtn');
const cancelBtn = document.getElementById('cancelBtn');
const concurrencyInput = document.getElementById('concurrency');
const overallBar = document.getElementById('overallBar');
const overallText = document.getElementById('overallText');
const fileList = document.getElementById('fileList');

let abortAll = new AbortController();

startBtn.addEventListener('click', start);
cancelBtn.addEventListener('click', () => abortAll.abort());

async function start(){
  startBtn.disabled = true;
  cancelBtn.disabled = false;
  abortAll = new AbortController();

  const meta = await (await fetch(`/PlexDirect/Urls?playlistKey=${encodeURIComponent(playlistKey)}`, { signal: abortAll.signal })).json();
  const items = meta.items || [];
  if (!items.length){
    overallText.textContent = (window.__i18nPlexDirect?.noItems || 'No items found.');
    startBtn.disabled = false; cancelBtn.disabled = true;
    return;
  }

  fileList.innerHTML = '';
  const ui = items.map((it, idx) => {
    const li = document.createElement('li');
    li.innerHTML = `
      <span class="file-name">${escapeHtml(it.filename)}</span>
      <span class="file-size">${prettyBytes(it.sizeBytes) || ''}</span>
      <div class="file-progress"><div id="p${idx}"></div></div>
    `;
    fileList.appendChild(li);
    return { ...it, bar: li.querySelector(`#p${idx}`), done: 0 };
  });

  const zip = new SimpleZipWriter();
  const concurrency = Math.max(1, Math.min(8, Number(concurrencyInput.value) || 3));
  const totalBytes = ui.reduce((s,x)=> s + (x.sizeBytes||0), 0);
  let completedBytes = 0, completedFiles = 0;
  const queue = [...ui];
  const errors = [];

  updateOverall();

  await Promise.all(Array.from({length: concurrency}, () => worker()));

  try {
    const blob = await zip.close();
    const name = safeName(`${playlistName || 'playlist'}-original.zip`);
    const url = URL.createObjectURL(blob);
    const a = Object.assign(document.createElement('a'), { href:url, download:name });
    document.body.appendChild(a); a.click(); a.remove();
    URL.revokeObjectURL(url);
    overallText.textContent = errors.length ? (window.__i18nPlexDirect?.finishedWithErrors || 'Finished with errors ({0}).').replace('{0}', errors.length) : (window.__i18nPlexDirect?.finished || 'Done. {0} files.').replace('{0}', ui.length);
  } catch (e) {
    console.error(e);
    overallText.textContent = (window.__i18nPlexDirect?.finishedWithErrors || 'Finished with errors');
  }

  startBtn.disabled = false;
  cancelBtn.disabled = true;

  async function worker(){
    while (queue.length && !abortAll.signal.aborted){
      const it = queue.shift();
      if (!it) break;

      let attempt=0, max=3, backoff=1000;
      while (attempt < max && !abortAll.signal.aborted){
        try {
          const buf = await downloadToBuffer(it.url, (delta)=>onDelta(it, delta), abortAll.signal);
          await zip.add(it.filename, buf);
          completedFiles++;
          break;
        } catch (err){
          attempt++;
          if (abortAll.signal.aborted) return;
          if (attempt>=max){
            errors.push({file: it.filename, error: String(err)});
            console.warn('Fehler', it.filename, err);
          } else {
            await sleep(backoff); backoff*=2;
          }
        }
      }
      updateOverall();
    }
  }

  function onDelta(it, delta){
    it.done += delta;
    if (it.sizeBytes && it.sizeBytes>0){
      const pct = Math.min(100, (it.done/it.sizeBytes)*100);
      it.bar.style.width = `${pct}%`;
    } else {
      const w = Number(it.bar.style.width.replace('%',''))||0;
      it.bar.style.width = ((w+5)%100)+'%';
    }
    completedBytes = ui.reduce((s,x)=> s + (x.done||0), 0);
    updateOverall();
  }

  function updateOverall(){
    const pct = totalBytes>0 ? Math.min(100, (completedBytes/totalBytes)*100) : (completedFiles/ui.length)*100;
    overallBar.style.width = `${pct}%`;
    overallText.textContent = `${completedFiles}/${ui.length} Dateien • ${prettyBytes(completedBytes)} von ${prettyBytes(totalBytes)}`;
  }
}

async function downloadToBuffer(url, onDelta, signal){
  const resp = await fetch(url, { signal });
  if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
  const reader = resp.body.getReader();
  const chunks = [];
  let len = 0;
  while (true){
    const {done, value} = await reader.read();
    if (done) break;
    chunks.push(value);
    len += value.byteLength;
    onDelta(value.byteLength);
  }
  const out = new Uint8Array(len);
  let o=0;
  for (const c of chunks){ out.set(c, o); o+=c.byteLength; }
  return out.buffer;
}

// utils
function sleep(ms){ return new Promise(r=>setTimeout(r, ms)); }
function prettyBytes(n){ if (!n || isNaN(n)) return ''; const u=['B','KB','MB','GB','TB']; let i=0; while(n>=1024&&i<u.length-1){n/=1024;i++;} return `${n.toFixed(n<10&&i>0?1:0)} ${u[i]}`; }
function escapeHtml(s){ return (s||'').replace(/[&<>"']/g, c=>({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;', "'":'&#39;'}[c])); }
function safeName(s){ return s.replace(/[\\/:*?"<>|]+/g,'_').trim(); }