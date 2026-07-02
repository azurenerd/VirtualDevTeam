// FlowMonitor live-log terminal.
// Owned by /flow-monitor-log.razor. Renders FlowMonitorEvent broadcasts from
// /hubs/flowmonitor into an xterm.js terminal styled to match the Copilot CLI:
//   ● purple — assistant / finding ("what the FlowMonitor noticed")
//   ● green  — tool / action started or succeeded
//   ● red    — error / failed action
//   ● cyan   — reasoning / detector running
//   ● gray   — lifecycle / tick boundaries / info
//
// Verbosity selector mirrors the LangSmith / telegram-app pattern:
//   LOW    — Lifecycle + Finding + Error only ("executive summary")
//   MEDIUM — + Detector + Action + ActionResult (default — operator view)
//   HIGH   — + Info (full firehose)
//
// The hub broadcasts every event regardless of verbosity; we filter client-side
// so a verbosity switch is instant and doesn't require reconfiguring the server.

(function () {
    'use strict';

    let term = null;
    let fitAddon = null;
    let connection = null;
    let verbosity = 1; // 0=low, 1=med, 2=high (matches FlowMonitorVerbosity enum)
    let totalReceived = 0;
    let totalRendered = 0;
    let dotNetRef = null;
    let resizeHandler = null;

    // ANSI escape sequences for foreground colors used in the FlowMonitor stream.
    // Bright variants pop more on the GH-dark theme already loaded by xterm.
    const C = {
        reset: '\x1b[0m',
        dim: '\x1b[2m',
        bold: '\x1b[1m',
        gray: '\x1b[90m',
        red: '\x1b[91m',
        green: '\x1b[92m',
        yellow: '\x1b[93m',
        blue: '\x1b[94m',
        magenta: '\x1b[95m', // Copilot purple
        cyan: '\x1b[96m',
        white: '\x1b[97m',
    };

    // Map FlowMonitorEventKind enum values (see Core/HealthMonitor/FlowMonitorEvent.cs)
    // to a verbosity gate. SignalR hands us numeric enum values when the payload is
    // serialized via System.Text.Json with default options.
    // Lifecycle=0, Detector=1, Finding=2, Action=3, ActionResult=4, Info=5, Error=6
    const KIND = {
        Lifecycle: 0,
        Detector: 1,
        Finding: 2,
        Action: 3,
        ActionResult: 4,
        Info: 5,
        Error: 6,
    };

    function shouldRender(kind) {
        // LOW: lifecycle + finding + error
        // MEDIUM: + detector + action + actionresult
        // HIGH: everything (including Info)
        if (verbosity >= 2) return true;
        if (verbosity >= 1) {
            return kind === KIND.Lifecycle || kind === KIND.Detector ||
                kind === KIND.Finding || kind === KIND.Action ||
                kind === KIND.ActionResult || kind === KIND.Error;
        }
        // LOW
        return kind === KIND.Lifecycle || kind === KIND.Finding || kind === KIND.Error;
    }

    function bullet(kind, evt) {
        // Match Copilot CLI conventions:
        //   Finding (assistant) — magenta ●
        //   Action / ActionResult.Success — green ●
        //   Error / ActionResult.Failed — red ●
        //   Detector (reasoning) — cyan ●
        //   Lifecycle / Info — dim gray ●
        if (kind === KIND.Error) return C.red + '●' + C.reset;
        if (kind === KIND.Finding) {
            // Critical findings get a red bullet so they pop even though they're
            // semantically "assistant text". Severity comes through as the enum
            // numeric: Info=0, Warning=1, Critical=2.
            if (evt && evt.severity === 2) return C.red + '●' + C.reset;
            if (evt && evt.severity === 1) return C.yellow + '●' + C.reset;
            return C.magenta + '●' + C.reset;
        }
        if (kind === KIND.Action) return C.green + '●' + C.reset;
        if (kind === KIND.ActionResult) {
            // ActionResult enum: Success=0, NoOp=1, Failed=2, Skipped=3
            if (evt && evt.actionResult === 2) return C.red + '●' + C.reset;
            if (evt && evt.actionResult === 3) return C.gray + '●' + C.reset;
            return C.green + '●' + C.reset;
        }
        if (kind === KIND.Detector) return C.cyan + '●' + C.reset;
        return C.gray + '●' + C.reset;
    }

    function fmtTime(iso) {
        // Server sends ISO-8601 UTC; render as local HH:mm:ss.fff for terminal compactness.
        try {
            const d = new Date(iso);
            const hh = String(d.getHours()).padStart(2, '0');
            const mm = String(d.getMinutes()).padStart(2, '0');
            const ss = String(d.getSeconds()).padStart(2, '0');
            const ms = String(d.getMilliseconds()).padStart(3, '0');
            return hh + ':' + mm + ':' + ss + '.' + ms;
        } catch (_) {
            return '??:??:??.???';
        }
    }

    function renderEvent(evt) {
        if (!term) return;
        if (!evt || typeof evt.kind !== 'number') return;
        totalReceived++;
        if (!shouldRender(evt.kind)) return;
        totalRendered++;

        const ts = C.dim + fmtTime(evt.timestamp) + C.reset;
        const src = C.bold + (evt.source || '?') + C.reset;
        const dot = bullet(evt.kind, evt);
        const msg = (evt.message || '').replace(/[\r\n]+/g, ' ');

        // Optional secondary metadata (agent / session) shown dimmed at the end of the line
        const tags = [];
        if (evt.agentId) tags.push('agent=' + evt.agentId);
        if (evt.sessionId) tags.push('sid=' + String(evt.sessionId).substring(0, 8));
        const tagStr = tags.length ? ' ' + C.dim + '[' + tags.join(' ') + ']' + C.reset : '';

        term.writeln(ts + ' ' + dot + ' ' + src + ' ' + msg + tagStr);

        // Detail (rationale / exception) on a continuation line if present, dimmed for low contrast.
        if (evt.detail && verbosity >= 1) {
            const detail = String(evt.detail).replace(/[\r\n]+/g, ' ');
            const truncated = detail.length > 200 ? detail.substring(0, 200) + '…' : detail;
            term.writeln('           ' + C.dim + '↳ ' + truncated + C.reset);
        }

        // Notify Blazor of the new totals for the header counter (rare-call → no perf concern).
        if (dotNetRef && (totalReceived % 10 === 0 || totalReceived <= 5)) {
            try { dotNetRef.invokeMethodAsync('OnCountersChanged', totalReceived, totalRendered); }
            catch (_) { /* circuit gone — ignore */ }
        }
    }

    async function startConnection(hubUrl) {
        if (typeof signalR === 'undefined') {
            term.writeln(C.red + '● fatal: @microsoft/signalr JS client not loaded' + C.reset);
            return;
        }
        connection = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .build();

        connection.on('FlowMonitorEvent', renderEvent);

        connection.onreconnecting(() => {
            term.writeln(C.yellow + '● reconnecting to flow-monitor hub…' + C.reset);
        });
        connection.onreconnected(() => {
            term.writeln(C.green + '● reconnected to flow-monitor hub' + C.reset);
        });
        connection.onclose((err) => {
            term.writeln(C.red + '● disconnected from flow-monitor hub' +
                (err ? ': ' + err.message : '') + C.reset);
        });

        try {
            await connection.start();
            await connection.invoke('Subscribe').catch(() => { /* server-side advisory only */ });
            term.writeln(C.green + '● connected to ' + hubUrl + C.reset);
        } catch (err) {
            term.writeln(C.red + '● failed to connect: ' + (err && err.message ? err.message : err) + C.reset);
        }
    }

    window.FlowMonitorLog = {
        /**
         * Initialize the terminal in the given container and connect to the hub.
         * @param {string} containerId
         * @param {string} hubUrl - absolute URL like 'http://localhost:5050/hubs/flowmonitor'
         * @param {object} objRef - DotNet object reference for callbacks (counters, etc.)
         * @param {number} initialVerbosity - 0/1/2
         */
        init: function (containerId, hubUrl, objRef, initialVerbosity) {
            verbosity = (typeof initialVerbosity === 'number') ? initialVerbosity : 1;
            dotNetRef = objRef || null;

            const container = document.getElementById(containerId);
            if (!container) return;
            // Idempotent: if a previous terminal was created (e.g., page revisit), tear it down.
            if (term) {
                try { term.dispose(); } catch (_) { }
                term = null;
            }
            container.innerHTML = '';

            term = new Terminal({
                cursorBlink: false,
                cursorStyle: 'bar',
                disableStdin: true,
                fontSize: 13,
                fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', 'Consolas', monospace",
                theme: {
                    background: '#0d1117',
                    foreground: '#c9d1d9',
                    cursor: '#00d4ff',
                    cursorAccent: '#0d1117',
                    selectionBackground: 'rgba(0, 212, 255, 0.3)',
                    black: '#484f58',
                    red: '#ff7b72',
                    green: '#3fb950',
                    yellow: '#d29922',
                    blue: '#58a6ff',
                    magenta: '#bc8cff',
                    cyan: '#00d4ff',
                    white: '#c9d1d9',
                    brightBlack: '#6e7681',
                    brightRed: '#ffa198',
                    brightGreen: '#56d364',
                    brightYellow: '#e3b341',
                    brightBlue: '#79c0ff',
                    brightMagenta: '#d2a8ff',
                    brightCyan: '#39d0ff',
                    brightWhite: '#f0f6fc'
                },
                scrollback: 10000,
                allowProposedApi: true
            });

            fitAddon = new FitAddon.FitAddon();
            term.loadAddon(fitAddon);
            term.open(container);
            setTimeout(() => { try { fitAddon.fit(); } catch (_) { } }, 50);

            // Banner
            term.writeln(C.cyan + '╭───────────────────────────────────────────────────────────╮' + C.reset);
            term.writeln(C.cyan + '│  ' + C.bold + C.white + 'FlowMonitor — live event stream' + C.reset + C.cyan + '                          │' + C.reset);
            term.writeln(C.cyan + '│  ' + C.dim + 'Mirrors Copilot CLI: ' + C.reset + C.magenta + '● ' + C.reset + 'finding · ' +
                C.green + '● ' + C.reset + 'action · ' + C.cyan + '● ' + C.reset + 'detector  ' + C.cyan + '│' + C.reset);
            term.writeln(C.cyan + '╰───────────────────────────────────────────────────────────╯' + C.reset);
            term.writeln('');

            resizeHandler = () => { try { fitAddon.fit(); } catch (_) { } };
            window.addEventListener('resize', resizeHandler);

            startConnection(hubUrl);
        },

        setVerbosity: function (v) {
            if (typeof v === 'number') verbosity = v;
        },

        clear: function () {
            if (term) term.clear();
            totalReceived = 0;
            totalRendered = 0;
        },

        dispose: function () {
            try { if (connection) connection.stop(); } catch (_) { }
            connection = null;
            try { if (term) term.dispose(); } catch (_) { }
            term = null;
            if (resizeHandler) window.removeEventListener('resize', resizeHandler);
            resizeHandler = null;
            dotNetRef = null;
            totalReceived = 0;
            totalRendered = 0;
        },
    };
})();
