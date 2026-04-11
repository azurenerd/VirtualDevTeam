(function () {
    'use strict';

    // Store terminal instances per thread
    const terminals = {};
    let dotNetRef = null;
    let activeThreadId = null;
    let fitAddon = null;

    window.DirectorCLI = {
        /**
         * Initialize the Director CLI with xterm.js
         * @param {string} containerId - DOM element ID for the terminal
         * @param {object} objRef - DotNet object reference for callbacks
         */
        init: function (containerId, objRef) {
            dotNetRef = objRef;
            // Pre-warm: create a default thread terminal when init is called
            window.addEventListener('resize', () => {
                if (activeThreadId && terminals[activeThreadId]) {
                    try { terminals[activeThreadId].fitAddon.fit(); } catch (_) { }
                }
            });
        },

        /**
         * Create or switch to a terminal for a specific thread
         */
        activateThread: function (threadId, containerId) {
            // Hide all existing terminals
            Object.keys(terminals).forEach(id => {
                if (terminals[id] && terminals[id].element) {
                    terminals[id].element.style.display = 'none';
                }
            });

            activeThreadId = threadId;

            // If terminal already exists, show it
            if (terminals[threadId]) {
                terminals[threadId].element.style.display = 'block';
                try { terminals[threadId].fitAddon.fit(); } catch (_) { }
                terminals[threadId].term.focus();
                return;
            }

            // Create new terminal
            const container = document.getElementById(containerId);
            if (!container) return;

            const termEl = document.createElement('div');
            termEl.id = 'term-' + threadId;
            termEl.style.height = '100%';
            termEl.style.width = '100%';
            container.appendChild(termEl);

            const term = new Terminal({
                cursorBlink: true,
                cursorStyle: 'bar',
                fontSize: 14,
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

            const fit = new FitAddon.FitAddon();
            term.loadAddon(fit);
            term.open(termEl);

            // Delay fit to ensure DOM is ready
            setTimeout(() => {
                try { fit.fit(); } catch (_) { }
            }, 100);

            // Track current input line
            let currentLine = '';

            term.onKey(function (ev) {
                const key = ev.key;
                const code = ev.domEvent.keyCode;

                if (code === 13) { // Enter
                    term.write('\r\n');
                    if (currentLine.trim()) {
                        dotNetRef.invokeMethodAsync('OnCommandEntered', threadId, currentLine.trim());
                    }
                    currentLine = '';
                } else if (code === 8) { // Backspace
                    if (currentLine.length > 0) {
                        currentLine = currentLine.slice(0, -1);
                        term.write('\b \b');
                    }
                } else if (code === 3 && ev.domEvent.ctrlKey) { // Ctrl+C
                    dotNetRef.invokeMethodAsync('OnCancelRequested', threadId);
                    currentLine = '';
                    term.write('^C\r\n');
                } else if (key.length === 1 && !ev.domEvent.ctrlKey && !ev.domEvent.altKey) {
                    currentLine += key;
                    term.write(key);
                }
            });

            // Paste support
            term.onData(function (data) {
                // Only handle paste (multi-char data that isn't a key event)
                if (data.length > 1 && !data.startsWith('\x1b')) {
                    currentLine += data;
                    term.write(data);
                }
            });

            // Welcome message
            term.writeln('\x1b[36m╔════════════════════════════════════════════╗\x1b[0m');
            term.writeln('\x1b[36m║    \x1b[1;37mAgentSquad Director CLI\x1b[0;36m               ║\x1b[0m');
            term.writeln('\x1b[36m║    \x1b[0;90mPowered by GitHub Copilot CLI\x1b[36m          ║\x1b[0m');
            term.writeln('\x1b[36m╚════════════════════════════════════════════╝\x1b[0m');
            term.writeln('');
            term.writeln('\x1b[90mType a question or command. Use Ctrl+C to cancel.\x1b[0m');
            term.writeln('\x1b[90mCreate new threads with the + button to run parallel queries.\x1b[0m');
            term.writeln('');

            terminals[threadId] = {
                term: term,
                fitAddon: fit,
                element: termEl
            };

            term.focus();
        },

        /**
         * Write output to a specific thread's terminal
         */
        writeOutput: function (threadId, text) {
            if (terminals[threadId]) {
                terminals[threadId].term.write(text);
            }
        },

        /**
         * Clear a thread's terminal
         */
        clearTerminal: function (threadId) {
            if (terminals[threadId]) {
                terminals[threadId].term.clear();
            }
        },

        /**
         * Remove a thread's terminal
         */
        removeThread: function (threadId) {
            if (terminals[threadId]) {
                terminals[threadId].term.dispose();
                if (terminals[threadId].element) {
                    terminals[threadId].element.remove();
                }
                delete terminals[threadId];
            }
        },

        /**
         * Fit all terminals to their containers
         */
        fitAll: function () {
            Object.keys(terminals).forEach(id => {
                try { terminals[id].fitAddon.fit(); } catch (_) { }
            });
        },

        /**
         * Cleanup
         */
        destroy: function () {
            Object.keys(terminals).forEach(id => {
                try { terminals[id].term.dispose(); } catch (_) { }
            });
            Object.keys(terminals).forEach(id => delete terminals[id]);
            dotNetRef = null;
            activeThreadId = null;
        }
    };
})();
