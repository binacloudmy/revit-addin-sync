// CadViewerHtml — embedded HTML for the CAD-to-BIM viewer.
// Served at GET /cad/viewer by McpServer.

namespace BinaVibe.Mcp
{
    internal static class CadViewerHtml
    {
        public const string Content = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>CAD-to-BIM Viewer</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, sans-serif; background: #1a1a2e; color: #eee; }
        .container { display: flex; height: 100vh; }
        .canvas-area { flex: 1; position: relative; }
        #cadCanvas { width: 100%; height: 100%; background: #0f0f23; }
        .sidebar { width: 320px; background: #16213e; display: flex; flex-direction: column; }
        .panel { padding: 16px; border-bottom: 1px solid #0f3460; }
        .panel h3 { font-size: 14px; margin-bottom: 12px; color: #e94560; }
        .layer-item { display: flex; align-items: center; gap: 8px; padding: 4px 0; }
        .layer-item input { accent-color: #e94560; }
        .layer-item label { font-size: 13px; }
        .chat-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
        .chat-messages { flex: 1; overflow-y: auto; padding: 12px; }
        .message { padding: 10px 12px; margin: 8px 0; border-radius: 8px; font-size: 13px; }
        .message.ai { background: #0f3460; }
        .message.user { background: #e94560; margin-left: 20px; }
        .message.error { background: #8b0000; }
        .chat-input { padding: 12px; border-top: 1px solid #0f3460; }
        .btn-group { display: flex; gap: 8px; flex-wrap: wrap; }
        .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; }
        .btn-primary { background: #e94560; color: #fff; }
        .btn-secondary { background: #0f3460; color: #fff; }
        .btn-create { width: 100%; padding: 12px; margin-top: 12px; }
        .btn:disabled { opacity: 0.5; cursor: not-allowed; }
        .status { padding: 8px 16px; background: #0f3460; font-size: 12px; }
        .config { font-size: 11px; color: #888; margin-top: 8px; }
        .config input { background: #0f3460; border: 1px solid #333; color: #eee; padding: 4px 8px; width: 100%; margin-top: 4px; border-radius: 4px; }
        .input-widget { margin-top: 8px; }
        .input-widget input[type=""text""] { width: 100%; padding: 8px; background: #0f3460; border: 1px solid #333; color: #eee; border-radius: 4px; }
        .input-widget input[type=""range""] { width: 100%; accent-color: #e94560; }
        .slider-value { text-align: center; font-size: 14px; margin-top: 4px; color: #e94560; }
        .input-widget button { margin-top: 8px; }
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""canvas-area"">
            <canvas id=""cadCanvas""></canvas>
        </div>
        <div class=""sidebar"">
            <div class=""panel"">
                <h3>Load DWG</h3>
                <div class=""config"">
                    <label>DWG File Path:</label>
                    <input type=""text"" id=""dwgPath"" placeholder=""C:\\path\\to\\file.dwg"">
                    <button class=""btn btn-primary"" id=""loadDwgBtn"" style=""margin-top:8px;width:100%"">Load DWG</button>
                </div>
                <div class=""config"" style=""margin-top:12px"">
                    <label>AI Server URL:</label>
                    <input type=""text"" id=""serverUrl"" placeholder=""https://your-ngrok-url"">
                </div>
            </div>
            <div class=""panel"">
                <h3>Layers</h3>
                <div id=""layerList""></div>
            </div>
            <div class=""panel chat-area"">
                <h3>AI Assistant</h3>
                <div class=""chat-messages"" id=""chatMessages""></div>
                <div class=""chat-input"">
                    <div class=""btn-group"" id=""responseButtons""></div>
                </div>
            </div>
            <div class=""panel"">
                <button class=""btn btn-primary btn-create"" id=""createBtn"" disabled>Create Walls in Revit</button>
            </div>
            <div class=""status"" id=""status"">Ready</div>
        </div>
    </div>

    <script>
        let sessionId = null;
        let layers = [];
        let lines = [];
        let arcs = [];
        let centerlines = [];
        let transform = { scale: 1, offsetX: 0, offsetY: 0 };
        let dragging = false;
        let lastMouse = { x: 0, y: 0 };

        // Addin MCP is always localhost (same machine)
        const ADDIN_URL = 'http://localhost:48820';
        // Get secret from URL param if provided
        const params = new URLSearchParams(window.location.search);
        const secret = params.get('secret') || '';

        function getServerUrl() {
            return document.getElementById('serverUrl').value.replace(/\/$/, '');
        }

        const canvas = document.getElementById('cadCanvas');
        const ctx = canvas.getContext('2d');

        function resizeCanvas() {
            canvas.width = canvas.offsetWidth * window.devicePixelRatio;
            canvas.height = canvas.offsetHeight * window.devicePixelRatio;
            ctx.scale(window.devicePixelRatio, window.devicePixelRatio);
            render();
        }
        window.addEventListener('resize', resizeCanvas);

        canvas.addEventListener('mousedown', e => { dragging = true; lastMouse = { x: e.clientX, y: e.clientY }; });
        canvas.addEventListener('mousemove', e => {
            if (!dragging) return;
            transform.offsetX += e.clientX - lastMouse.x;
            transform.offsetY += e.clientY - lastMouse.y;
            lastMouse = { x: e.clientX, y: e.clientY };
            render();
        });
        canvas.addEventListener('mouseup', () => dragging = false);
        canvas.addEventListener('wheel', e => { e.preventDefault(); transform.scale *= e.deltaY > 0 ? 0.9 : 1.1; render(); });

        function render() {
            const w = canvas.offsetWidth, h = canvas.offsetHeight;
            ctx.fillStyle = '#0f0f23';
            ctx.fillRect(0, 0, w, h);
            ctx.save();
            ctx.translate(transform.offsetX + w/2, transform.offsetY + h/2);
            ctx.scale(transform.scale, -transform.scale);

            ctx.strokeStyle = '#888';
            ctx.lineWidth = 1 / transform.scale;
            for (const line of lines) {
                if (!isLayerVisible(line.layer)) continue;
                ctx.beginPath(); ctx.moveTo(line.x1, line.y1); ctx.lineTo(line.x2, line.y2); ctx.stroke();
            }
            for (const arc of arcs) {
                if (!isLayerVisible(arc.layer)) continue;
                ctx.beginPath(); ctx.arc(arc.cx, arc.cy, arc.r, arc.start_deg * Math.PI / 180, arc.end_deg * Math.PI / 180); ctx.stroke();
            }
            if (centerlines.length > 0) {
                ctx.strokeStyle = '#00ff00';
                ctx.lineWidth = 3 / transform.scale;
                for (const cl of centerlines) { ctx.beginPath(); ctx.moveTo(cl.ax, cl.ay); ctx.lineTo(cl.bx, cl.by); ctx.stroke(); }
            }
            ctx.restore();
        }

        function isLayerVisible(name) {
            const cb = document.querySelector(`input[data-layer=""${name}""]`);
            return cb ? cb.checked : true;
        }

        function fitToExtents() {
            if (lines.length === 0) return;
            let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
            for (const line of lines) { minX = Math.min(minX, line.x1, line.x2); minY = Math.min(minY, line.y1, line.y2); maxX = Math.max(maxX, line.x1, line.x2); maxY = Math.max(maxY, line.y1, line.y2); }
            const w = canvas.offsetWidth, h = canvas.offsetHeight;
            transform.scale = 0.9 * Math.min(w / (maxX - minX || 1), h / (maxY - minY || 1));
            transform.offsetX = -(minX + maxX) / 2 * transform.scale;
            transform.offsetY = (minY + maxY) / 2 * transform.scale;
            render();
        }

        async function callAddinTool(toolName, args) {
            const headers = { 'Content-Type': 'application/json' };
            if (secret) headers['X-Bina-Secret'] = secret;
            const resp = await fetch(`${ADDIN_URL}/mcp/tools/${toolName}`, { method: 'POST', headers, body: JSON.stringify({ args }) });
            if (!resp.ok) throw new Error(await resp.text());
            return resp.json();
        }

        async function callServer(endpoint, body) {
            const serverUrl = getServerUrl();
            if (!serverUrl) throw new Error('Set AI Server URL first');
            const resp = await fetch(`${serverUrl}${endpoint}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'ngrok-skip-browser-warning': '1' },
                body: JSON.stringify(body)
            });
            if (!resp.ok) { const d = await resp.json().catch(() => ({})); throw new Error(d.detail || `Server error: ${resp.status}`); }
            return resp.json();
        }

        async function loadCAD(dwgRef) {
            try {
                setStatus('Creating session...');
                const sess = await callServer('/cad/session', { dwg_ref: dwgRef });
                sessionId = sess.session_id;
                addMessage('ai', `Session: ${sessionId}`);

                setStatus('Loading DWG...');
                const load = await callAddinTool('cad_load', { dwg_ref: dwgRef });
                if (!load.ok) throw new Error(load.error);
                layers = load.layers || [];
                renderLayers();
                await callServer('/cad/ingest/layers', { session_id: sessionId, layers: load.layers, entity_counts: load.entity_counts });

                setStatus('Loading geometry...');
                const geo = await callAddinTool('cad_get_lines', { dwg_ref: dwgRef });
                if (!geo.ok) throw new Error(geo.error);
                lines = geo.lines || []; arcs = geo.arcs || [];
                await callServer('/cad/ingest/geometry', { session_id: sessionId, lines, arcs });

                resizeCanvas(); fitToExtents();
                setStatus(`Loaded: ${lines.length} lines, ${arcs.length} arcs`);
                await askNextQuestion();
            } catch (err) { addMessage('error', err.message); setStatus('Error: ' + err.message); }
        }

        // --- AI Clarification Flow ---
        let currentQuestion = null;

        async function askNextQuestion() {
            try {
                setStatus('AI thinking...');
                const result = await callServer('/cad/ask', { session_id: sessionId });

                if (result.done) {
                    addMessage('ai', 'All set! Click Create Walls when ready.');
                    document.getElementById('createBtn').disabled = false;
                    setStatus('Ready to create');
                    return;
                }

                currentQuestion = result.question;
                addMessage('ai', currentQuestion.question);

                // Show preview if available
                if (result.preview) {
                    centerlines = result.preview.centerlines || [];
                    render();
                }

                renderQuestionInput(currentQuestion);
                setStatus('Waiting for your answer...');
            } catch (err) { addMessage('error', err.message); setStatus('Error'); }
        }

        function renderQuestionInput(q) {
            clearButtons();
            const container = document.getElementById('responseButtons');

            if (q.type === 'choice' || q.type === 'multi_choice') {
                // Button choices
                const opts = q.options || [];
                opts.forEach(opt => {
                    const btn = document.createElement('button');
                    btn.className = 'btn btn-secondary';
                    btn.textContent = opt;
                    btn.onclick = () => submitAnswer(q.key, opt);
                    container.appendChild(btn);
                });
            } else if (q.type === 'slider') {
                // Slider input
                const widget = document.createElement('div');
                widget.className = 'input-widget';
                const slider = document.createElement('input');
                slider.type = 'range';
                slider.min = q.min || 0;
                slider.max = q.max || 100;
                slider.value = q.default || q.min || 0;
                const valueLabel = document.createElement('div');
                valueLabel.className = 'slider-value';
                valueLabel.textContent = slider.value + (q.unit || '');
                slider.oninput = () => { valueLabel.textContent = slider.value + (q.unit || ''); };
                const submitBtn = document.createElement('button');
                submitBtn.className = 'btn btn-primary';
                submitBtn.textContent = 'Confirm';
                submitBtn.onclick = () => submitAnswer(q.key, parseInt(slider.value));
                widget.appendChild(slider);
                widget.appendChild(valueLabel);
                widget.appendChild(submitBtn);
                container.appendChild(widget);
            } else if (q.type === 'text') {
                // Text input
                const widget = document.createElement('div');
                widget.className = 'input-widget';
                const input = document.createElement('input');
                input.type = 'text';
                input.placeholder = 'Type your answer...';
                input.onkeypress = (e) => { if (e.key === 'Enter') submitAnswer(q.key, input.value); };
                const submitBtn = document.createElement('button');
                submitBtn.className = 'btn btn-primary';
                submitBtn.textContent = 'Submit';
                submitBtn.onclick = () => submitAnswer(q.key, input.value);
                widget.appendChild(input);
                widget.appendChild(submitBtn);
                container.appendChild(widget);
            } else if (q.type === 'confirm') {
                // Confirm buttons
                const yesBtn = document.createElement('button');
                yesBtn.className = 'btn btn-primary';
                yesBtn.textContent = 'Yes, create walls';
                yesBtn.onclick = () => submitAnswer(q.key, true);
                const noBtn = document.createElement('button');
                noBtn.className = 'btn btn-secondary';
                noBtn.textContent = 'Go back';
                noBtn.onclick = () => { addMessage('user', 'Let me reconsider...'); /* TODO: implement back */ };
                container.appendChild(yesBtn);
                container.appendChild(noBtn);
            }
        }

        async function submitAnswer(key, value) {
            addMessage('user', String(value));
            clearButtons();
            setStatus('Processing...');

            try {
                const result = await callServer('/cad/answer', { session_id: sessionId, key, value });

                if (result.ready) {
                    // Ready to create walls
                    centerlines = result.centerlines || [];
                    render();
                    addMessage('ai', `Ready to create ${result.count} walls!`);
                    document.getElementById('createBtn').disabled = false;
                    setStatus('Ready to create');
                    return;
                }

                if (result.next_question) {
                    currentQuestion = result.next_question;
                    addMessage('ai', currentQuestion.question);

                    // Show preview if available
                    if (result.preview) {
                        centerlines = result.preview.centerlines || [];
                        render();
                    }

                    renderQuestionInput(currentQuestion);
                    setStatus('Waiting for your answer...');
                }
            } catch (err) { addMessage('error', err.message); setStatus('Error'); }
        }

        async function createWalls() {
            setStatus('Creating walls...'); document.getElementById('createBtn').disabled = true;
            try {
                const create = await callAddinTool('cad_create_walls', { centerlines });
                if (!create.ok) throw new Error(create.error);
                addMessage('ai', `Created ${create.count} walls!`); setStatus('Done'); centerlines = []; render();
            } catch (err) { addMessage('error', err.message); setStatus('Error'); document.getElementById('createBtn').disabled = false; }
        }

        function renderLayers() {
            document.getElementById('layerList').innerHTML = layers.map(n => `<div class=""layer-item""><input type=""checkbox"" checked data-layer=""${n}"" onchange=""render()""><label>${n}</label></div>`).join('');
        }
        function addMessage(type, text) { const d = document.createElement('div'); d.className = 'message ' + type; d.textContent = text; const c = document.getElementById('chatMessages'); c.appendChild(d); c.scrollTop = c.scrollHeight; }
        function showButtons(opts) { const c = document.getElementById('responseButtons'); c.innerHTML = ''; opts.forEach(o => { const b = document.createElement('button'); b.className = 'btn btn-secondary'; b.textContent = o.label; b.onclick = o.action; c.appendChild(b); }); }
        function clearButtons() { document.getElementById('responseButtons').innerHTML = ''; }
        function setStatus(t) { document.getElementById('status').textContent = t; }

        document.getElementById('createBtn').onclick = createWalls;
        document.getElementById('loadDwgBtn').onclick = loadDwgFromPath;
        resizeCanvas();

        async function loadDwgFromPath() {
            const path = document.getElementById('dwgPath').value.trim();
            if (!path) { addMessage('error', 'Enter a DWG file path'); return; }
            setStatus('Opening DWG...');
            try {
                const result = await callAddinTool('dwg_open_attachment', { path });
                if (!result.ok) throw new Error(result.error || 'Failed to open DWG');
                addMessage('ai', 'Opened: ' + (result.name || path));
                await loadCAD(result.dwg_ref);
            } catch (err) { addMessage('error', err.message); setStatus('Error: ' + err.message); }
        }

        const dwgRef = params.get('dwg_ref');
        if (dwgRef) { loadCAD(dwgRef); } else { addMessage('ai', 'Enter a DWG file path above and click Load DWG, or use AI Server URL for classification.'); setStatus('Ready'); }
    </script>
</body>
</html>";
    }
}
