namespace SCSKiller.Linux.App;

public static class UiAssets
{
    public static string GetHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Shaderless — shader stutter killer for Linux</title>
  <style>
    :root {
      --bg: #0d1117;
      --card-bg: #161b22;
      --card-border: #30363d;
      --text-main: #f0f6fc;
      --text-muted: #8b949e;
      --accent: #58a6ff;
      --accent-hover: #79c0ff;
      --green: #238636;
      --green-glow: #2ea043;
      --orange: #d29922;
      --red: #f85149;
      --purple: #bc8cff;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; }
    body { background-color: var(--bg); color: var(--text-main); height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
    
    /* Top Header */
    header {
      background-color: var(--card-bg);
      border-bottom: 1px solid var(--card-border);
      padding: 14px 24px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      user-select: none;
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .brand-icon {
      width: 32px;
      height: 32px;
      background: linear-gradient(135deg, #a371f7, #388bfd);
      border-radius: 8px;
      display: flex;
      align-items: center;
      justify-content: center;
      font-weight: 900;
      font-size: 18px;
      color: #fff;
      box-shadow: 0 0 12px rgba(163, 113, 247, 0.4);
    }
    .brand h1 { font-size: 19px; font-weight: 700; letter-spacing: 0.5px; }
    .brand-subtitle { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 1px; }

    /* Driver Status Pill */
    .driver-pill {
      background-color: #21262d;
      border: 1px solid var(--card-border);
      border-radius: 20px;
      padding: 6px 16px;
      display: flex;
      align-items: center;
      gap: 12px;
      font-size: 12px;
    }
    .status-dot { width: 8px; height: 8px; border-radius: 50%; background-color: #3fb950; box-shadow: 0 0 8px #3fb950; }
    .driver-meta { color: var(--text-muted); }
    .driver-meta strong { color: var(--text-main); }

    /* Action Buttons */
    .header-actions { display: flex; gap: 10px; }
    button {
      cursor: pointer;
      border-radius: 6px;
      padding: 8px 14px;
      font-size: 13px;
      font-weight: 600;
      border: 1px solid transparent;
      transition: all 0.15s ease;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .btn-primary { background-color: var(--green); color: #fff; border-color: rgba(240, 246, 252, 0.1); }
    .btn-primary:hover { background-color: var(--green-glow); box-shadow: 0 0 10px rgba(46, 160, 67, 0.4); }
    .btn-secondary { background-color: #21262d; color: var(--text-main); border-color: var(--card-border); }
    .btn-secondary:hover { background-color: #30363d; }
    .btn-danger { background-color: rgba(248, 81, 73, 0.15); color: #f85149; border-color: rgba(248, 81, 73, 0.4); }
    .btn-danger:hover { background-color: #f85149; color: #fff; }

    /* Main Container */
    main {
      flex: 1;
      padding: 24px;
      overflow-y: auto;
    }
    .section-title {
      font-size: 14px;
      font-weight: 600;
      color: var(--text-muted);
      text-transform: uppercase;
      letter-spacing: 1px;
      margin-bottom: 16px;
      display: flex;
      justify-content: space-between;
      align-items: center;
    }

    /* Game Cards Grid */
    .game-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
      gap: 16px;
    }
    .game-card {
      background-color: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 10px;
      padding: 16px;
      display: flex;
      flex-direction: column;
      gap: 10px;
      position: relative;
      transition: transform 0.15s ease, border-color 0.15s ease;
    }
    .game-card:hover {
      border-color: #58a6ff55;
      transform: translateY(-2px);
    }
    .card-top {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 10px;
    }
    .game-title {
      font-size: 16px;
      font-weight: 600;
      line-height: 1.3;
    }
    .badges {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      margin-top: 4px;
    }
    .badge {
      font-size: 11px;
      font-weight: 600;
      padding: 2px 8px;
      border-radius: 12px;
      text-transform: uppercase;
    }
    .badge-steam { background: #1b2838; color: #66c0f4; border: 1px solid #66c0f455; }
    .badge-heroic { background: #33152c; color: #f47bcf; border: 1px solid #f47bcf55; }
    .badge-lutris { background: #38240f; color: #f5a623; border: 1px solid #f5a62355; }
    .badge-engine { background: #21262d; color: var(--text-muted); border: 1px solid var(--card-border); }

    .status-pill {
      font-size: 11px;
      font-weight: 700;
      padding: 4px 10px;
      border-radius: 6px;
      white-space: nowrap;
    }
    .status-cached { background: rgba(35, 134, 54, 0.2); color: #3fb950; border: 1px solid #3fb95055; }
    .status-uncompiled { background: rgba(210, 153, 34, 0.2); color: #d29922; border: 1px solid #d2992255; }
    .status-compiling { background: rgba(88, 166, 255, 0.2); color: #58a6ff; border: 1px solid #58a6ff55; animation: pulse 1.5s infinite; }

    @keyframes pulse {
      0% { opacity: 0.7; }
      50% { opacity: 1; }
      100% { opacity: 0.7; }
    }

    .meta-row {
      display: flex;
      justify-content: space-between;
      font-size: 12px;
      color: var(--text-muted);
    }
    .meta-row strong { color: var(--text-main); }

    /* Progress Bar */
    .progress-box {
      display: none;
      flex-direction: column;
      gap: 6px;
    }
    .progress-track {
      width: 100%;
      height: 8px;
      background: #21262d;
      border-radius: 4px;
      overflow: hidden;
    }
    .progress-fill {
      height: 100%;
      width: 0%;
      background: linear-gradient(90deg, #388bfd, #a371f7);
      border-radius: 4px;
      transition: width 0.15s ease;
    }
    .progress-text {
      font-size: 11px;
      color: var(--text-muted);
      display: flex;
      justify-content: space-between;
    }

    .card-actions {
      display: flex;
      justify-content: flex-end;
      margin-top: 4px;
    }

    /* Loading Spinner */
    .spinner {
      display: inline-block;
      width: 20px;
      height: 20px;
      border: 3px solid rgba(255,255,255,.2);
      border-radius: 50%;
      border-top-color: #58a6ff;
      animation: spin 1s ease-in-out infinite;
    }
    @keyframes spin {
      to { transform: rotate(360deg); }
    }
  </style>
</head>
<body>
  <header>
    <div class="brand">
      <div class="brand-icon"><svg viewBox="130 58 252 396" width="14" height="22"><path fill="#fff" stroke="#fff" stroke-width="10" stroke-linejoin="round" d="M296 64 L136 288 H240 L208 448 L376 208 H272 Z"/></svg></div>
      <div>
        <h1>Shaderless</h1>
        <div class="brand-subtitle">Unofficial Linux port of SCSKiller · Beta</div>
      </div>
    </div>

    <div class="driver-pill" id="driver-pill">
      <div class="status-dot"></div>
      <div class="driver-meta">Scanning GPU & Driver...</div>
    </div>

    <div class="header-actions">
      <button class="btn-secondary" id="btn-rescan" onclick="scanGames()">🔄 Rescan</button>
      <button class="btn-primary" onclick="compileAll()">⚡ Pre-Compile All</button>
      <button class="btn-danger" onclick="clearCache()" title="Purge ALL compiled shaders from GPU driver and Steam game caches">🗑️ Clear All Caches</button>
    </div>
  </header>

  <main>
    <div class="section-title">
      <span id="library-count">Installed Games</span>
      <span style="font-size: 11px; color: var(--text-muted);">Shaders are compiled directly into GPU driver cache ahead of time</span>
    </div>

    <div class="game-grid" id="game-grid">
      <div style="color: var(--text-muted); padding: 40px; display: flex; align-items: center; gap: 12px;">
        <div class="spinner"></div>
        <span>Scanning game libraries (Steam, Heroic, Lutris)...</span>
      </div>
    </div>
  </main>

  <script>
    let currentGames = [];

    function post(data) {
      if (window.external && window.external.sendMessage) {
        window.external.sendMessage(JSON.stringify(data));
      }
    }

    function scanGames() {
      const btn = document.getElementById('btn-rescan');
      if (btn) btn.innerText = '🔄 Scanning...';
      post({ action: 'scan' });
    }

    function compileGame(id) {
      updateProgress({ gameId: id, percentage: 0, currentAction: 'Starting...', isComplete: false });
      post({ action: 'compile', gameId: id });
    }

    function compileAll() {
      post({ action: 'compile-all' });
    }

    function toggleRecorder(id) {
      post({ action: 'toggle-recorder', gameId: id });
    }

    function clearCache() {
      if (confirm('Purge ALL compiled shaders from GPU driver and Steam game caches?\n\nThis will reset cache for all games to 0 B.')) {
        post({ action: 'clear-all' });
      }
    }

    function clearGameCache(id, name) {
      if (confirm(`Delete on-disk shader cache for "${name}"?\n\nThis will reset the game's shader status to Needs Pre-Compilation (0 B).`)) {
        post({ action: 'clear-game', gameId: id });
      }
    }

    function formatBytes(bytes) {
      if (!bytes || bytes === 0) return '0 B';
      const k = 1024;
      const dm = 2;
      const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + ' ' + sizes[i];
    }

    function onMessage(raw) {
      try {
        const msg = (typeof raw === 'string') ? JSON.parse(raw) : raw;
        if (msg.type === 'state') {
          updateState(msg);
          post({ action: 'ack' });
        } else if (msg.type === 'progress') {
          updateProgress(msg);
        } else if (msg.type === 'error') {
          alert(msg.message);
        }
      } catch (e) {
        console.error('Error handling message from backend:', e, raw);
      }
    }

    function updateState(state) {
      const btn = document.getElementById('btn-rescan');
      if (btn) btn.innerText = '🔄 Rescan';

      if (state.driver) {
        const d = state.driver;
        const totalMb = formatBytes(d.totalCacheSizeBytes || (d.cacheSizeBytes + (d.steamShaderCacheSizeBytes || 0)));
        document.getElementById('driver-pill').innerHTML = `
          <div class="status-dot"></div>
          <div class="driver-meta">
            <strong>${d.deviceName}</strong> • ${d.driverName} (v${d.driverVersion}) • Total GPU Cache: <strong>${totalMb}</strong>
          </div>
        `;
      }

      if (state.games) {
        currentGames = state.games;
        document.getElementById('library-count').innerText = `Installed Games (${currentGames.length})`;
        renderGames();
      }
    }

    function updateProgress(p) {
      const card = document.getElementById(`game-${p.gameId}`);
      if (!card) return;

      const pBox = card.querySelector('.progress-box');
      const pFill = card.querySelector('.progress-fill');
      const pText = card.querySelector('.progress-text');
      const pStatus = card.querySelector('.status-pill');
      const btn = card.querySelector('.btn-primary');

      pBox.style.display = 'flex';
      pFill.style.width = `${p.percentage}%`;
      pText.innerHTML = `<span>${p.currentAction}</span><span>${p.percentage}%</span>`;

      if (p.isComplete) {
        pStatus.className = 'status-pill status-cached';
        pStatus.innerText = 'Cached (Ready)';
        btn.innerText = '⚡ Re-Compile';
        btn.disabled = false;
        setTimeout(() => { pBox.style.display = 'none'; }, 3000);
      } else {
        pStatus.className = 'status-pill status-compiling';
        pStatus.innerText = 'Compiling...';
        btn.disabled = true;
      }
    }

    function getEngineName(code) {
      const engines = ['Unknown', 'UnrealEngine4', 'UnrealEngine5', 'Unity', 'RedEngine', 'FromSoftware', 'ReEngine', 'Northlight', 'DirectX12', 'DirectX11'];
      return typeof code === 'number' ? (engines[code] || 'DirectX') : code;
    }

    function renderGames() {
      const grid = document.getElementById('game-grid');
      if (!currentGames || currentGames.length === 0) {
        grid.innerHTML = '<div style="color: var(--text-muted); padding: 20px;">No games detected in Steam, Heroic, or Lutris libraries.</div>';
        return;
      }

      grid.innerHTML = currentGames.map(g => {
        const isCached = g.state === 1 || g.state === 'Cached';
        const statusClass = isCached ? 'status-cached' : 'status-uncompiled';
        const statusLabel = isCached ? 'Cached (Ready)' : 'Needs Pre-Compilation';
        const sourceBadge = g.source === 0 || g.source === 'Steam' ? 'badge-steam' : (g.source === 1 || g.source === 'Heroic' ? 'badge-heroic' : 'badge-lutris');
        const sourceName = g.source === 0 || g.source === 'Steam' ? 'Steam' : (g.source === 1 || g.source === 'Heroic' ? 'Heroic' : 'Lutris');
        const engineLabel = getEngineName(g.engine);
        const cacheSizeLabel = formatBytes(g.cacheSizeBytes);

        return `
          <div class="game-card" id="game-${g.id}">
            <div class="card-top">
              <div>
                <div class="game-title">${g.name}</div>
                <div class="badges">
                  <span class="badge ${sourceBadge}">${sourceName}</span>
                  <span class="badge badge-engine">${engineLabel}</span>
                </div>
              </div>
              <div class="status-pill ${statusClass}">${statusLabel}</div>
            </div>

            <div class="meta-row">
              <span>Estimated Shaders:</span>
              <strong>~${(g.totalShadersEstimated || 5000).toLocaleString()}</strong>
            </div>

            <div class="meta-row">
              <span>On-Disk Shader Cache:</span>
              <strong style="${g.cacheSizeBytes > 0 ? 'color: #3fb950;' : ''}">${cacheSizeLabel}</strong>
            </div>

            <div class="meta-row">
              <span>Recorded Pipelines:</span>
              <strong style="${g.recordedPsos > 0 ? 'color: #bc8cff;' : ''}">${(g.recordedPsos || 0).toLocaleString()}${g.recorderInstalled ? ' • recording' : ''}</strong>
            </div>

            <div class="progress-box">
              <div class="progress-track"><div class="progress-fill"></div></div>
              <div class="progress-text"><span>Initializing...</span><span>0%</span></div>
            </div>

            <div class="card-actions" style="display: flex; gap: 8px; justify-content: flex-end;">
              ${g.cacheSizeBytes > 0 ? `
              <button class="btn-danger" style="padding: 7px 12px; font-size: 12px;" onclick="clearGameCache('${g.id}', '${g.name.replace(/'/g, "\\'")}')" title="Delete compiled shader cache on disk for this game">
                🗑️ Clear Cache
              </button>` : ''}
              <button class="btn-secondary" style="padding: 7px 12px; font-size: 12px; ${g.recorderInstalled ? 'border-color: #bc8cff; color: #bc8cff;' : ''}" onclick="toggleRecorder('${g.id}')" title="Record mode: a d3d12.dll next to the game records every pipeline it creates while you play, so the next compile covers exactly what the game uses">
                ${g.recorderInstalled ? '⏺ Recording (on)' : '⏺ Record'}
              </button>
              <button class="btn-primary" onclick="compileGame('${g.id}')">
                ${isCached ? '⚡ Re-Compile' : '⚡ Kill Stutter'}
              </button>
            </div>
          </div>
        `;
      }).join('');
    }

    // Set up bidirectional bridge
    let bridgeReady = false;
    let stateReceived = false;
    const origUpdateState = updateState;
    updateState = function(state) {
      stateReceived = true;
      origUpdateState(state);
    };
    function setupBridge() {
      if (window.external && window.external.receiveMessage) {
        window.external.receiveMessage(onMessage);
        bridgeReady = true;
        scanGames();
      } else {
        setTimeout(setupBridge, 30);
      }
    }
    setupBridge();

    // Safety fallback: if state hasn't arrived yet, request it again
    const retryInterval = setInterval(() => {
      if (!stateReceived) {
        scanGames();
      } else {
        clearInterval(retryInterval);
      }
    }, 1500);
  </script>
</body>
</html>
""";
}
