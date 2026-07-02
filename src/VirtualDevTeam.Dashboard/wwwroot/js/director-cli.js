(function () {
    'use strict';

    const terminals = {};
    let dotNetRef = null;
    let activeThreadId = null;

    // Tool type mappings (tool name → display type)
    var TOOL_TYPES = {
        'powershell': 'shell', 'view': 'file', 'edit': 'file', 'create': 'file',
        'grep': 'search', 'glob': 'file', 'sql': 'database', 'task': 'agent',
        'web_fetch': 'web', 'ask_user': 'chat', 'skill': 'agent',
        'store_memory': 'memory', 'vote_memory': 'memory', 'session_store_sql': 'database',
        'manage_schedule': 'schedule', 'fetch_copilot_cli_documentation': 'docs'
    };

    // Meta-tools that should be suppressed (not shown as tool calls)
    var HIDDEN_TOOLS = ['report_intent', 'task_complete'];

    // Generate human-readable description from tool name + args
    function toolDescription(name, args) {
        var parsed = null;
        if (args) {
            try { parsed = (typeof args === 'string') ? JSON.parse(args) : args; } catch (_) { }
        }
        if (parsed && parsed.description) return truncate(parsed.description, 70);

        if (!parsed) return humanize(name);

        switch (name) {
            case 'powershell': return truncate(parsed.command || 'Run command', 70);
            case 'view': return 'Read ' + basename(parsed.path || '');
            case 'edit': return 'Edit ' + basename(parsed.path || '');
            case 'create': return 'Create ' + basename(parsed.path || '');
            case 'grep': return 'Search for "' + truncate(parsed.pattern || '', 40) + '"';
            case 'glob': return 'Find ' + truncate(parsed.pattern || '*', 40);
            case 'sql': return truncate(parsed.description || parsed.query || 'Run query', 60);
            case 'task': return truncate(parsed.description || parsed.prompt || 'Run agent', 60);
            case 'web_fetch': return 'Fetch ' + truncate(parsed.url || '', 50);
            case 'ask_user': return truncate(parsed.question || 'Ask user', 60);
            case 'session_store_sql': return truncate(parsed.description || 'Query store', 60);
            default: return humanize(name);
        }
    }

    function humanize(name) {
        return name.replace(/_/g, ' ').replace(/\b\w/g, function (c) { return c.toUpperCase(); });
    }

    function truncate(s, max) {
        if (!s) return '';
        return s.length > max ? s.slice(0, max - 1) + '\u2026' : s;
    }

    function basename(path) {
        if (!path) return '';
        var parts = path.replace(/\\/g, '/').split('/');
        return parts[parts.length - 1] || path;
    }

    // Markdown-to-ANSI conversion
    function mdLine(line) {
        if (line.startsWith('#### ')) return '\x1b[1;36m' + line.slice(5) + '\x1b[0m';
        if (line.startsWith('### ')) return '\x1b[1;36m' + line.slice(4) + '\x1b[0m';
        if (line.startsWith('## ')) return '\x1b[1;96m' + line.slice(3) + '\x1b[0m';
        if (line.startsWith('# ')) return '\x1b[1;97m' + line.slice(2) + '\x1b[0m';
        if (/^\s*(---|\*\*\*|___)\s*$/.test(line))
            return '\x1b[90m' + '\u2500'.repeat(60) + '\x1b[0m';
        if (line.startsWith('> '))
            return '\x1b[90m\u258e\x1b[0m \x1b[3;37m' + mdInline(line.slice(2)) + '\x1b[0m';

        // Table separator row: |---|---|
        if (/^\|[\s\-:]+(\|[\s\-:]+)+\|?\s*$/.test(line)) {
            var cols = line.split('|').filter(function (c) { return c.trim() !== ''; });
            return '\x1b[90m' + cols.map(function (c) {
                return '\u2500'.repeat(c.length);
            }).join('\u253c') + '\x1b[0m';
        }

        // Table data row: | cell | cell |
        if (/^\|(.+\|)+\s*$/.test(line)) {
            var cells = line.split('|').filter(function (c) { return c !== ''; });
            // Remove trailing empty from final |
            if (cells.length > 0 && cells[cells.length - 1].trim() === '') cells.pop();
            return cells.map(function (c) {
                return mdInline(c);
            }).join('\x1b[90m\u2502\x1b[0m');
        }

        var trimmed = line.trimStart();
        var indent = line.length - trimmed.length;
        if (trimmed.startsWith('- ') || trimmed.startsWith('* ')) {
            return ' '.repeat(indent) + '\x1b[36m\u2022\x1b[0m ' + mdInline(trimmed.slice(2));
        }
        if (/^\d+\.\s/.test(trimmed)) {
            var m = trimmed.match(/^(\d+\.)\s(.*)/);
            if (m) return ' '.repeat(indent) + '\x1b[36m' + m[1] + '\x1b[0m ' + mdInline(m[2]);
        }
        return mdInline(line);
    }

    function mdInline(text) {
        text = text.replace(/\*\*(.+?)\*\*/g, '\x1b[1;97m$1\x1b[0m');
        text = text.replace(/`([^`]+)`/g, '\x1b[33m$1\x1b[0m');
        text = text.replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, '\x1b[3m$1\x1b[0m');
        return text;
    }

    function createStreamRenderer(term) {
        var inCodeBlock = false;
        var codeBlockLang = '';
        var lineBuffer = '';
        var tableBuffer = []; // buffered table rows for aligned rendering
        var isFirstText = true; // track for green response bullet

        function flushTable() {
            if (tableBuffer.length === 0) return;
            // Calculate column widths
            var colWidths = [];
            for (var r = 0; r < tableBuffer.length; r++) {
                var cells = splitTableRow(tableBuffer[r]);
                for (var c = 0; c < cells.length; c++) {
                    var w = cells[c].trim().length;
                    if (!colWidths[c] || w > colWidths[c]) colWidths[c] = w;
                }
            }
            // Render aligned table
            for (var r = 0; r < tableBuffer.length; r++) {
                var row = tableBuffer[r];
                // Separator row
                if (/^\|[\s\-:]+(\|[\s\-:]+)+\|?\s*$/.test(row)) {
                    var sep = colWidths.map(function (w) { return '\u2500'.repeat(w + 2); }).join('\u253c');
                    term.writeln('\x1b[90m' + sep + '\x1b[0m');
                    continue;
                }
                // Data row
                var cells = splitTableRow(row);
                var rendered = '';
                for (var c = 0; c < cells.length; c++) {
                    var cell = cells[c].trim();
                    var padded = cell + ' '.repeat(Math.max(0, (colWidths[c] || 0) - cell.length));
                    if (c > 0) rendered += ' \x1b[90m\u2502\x1b[0m ';
                    rendered += (r === 0) ? '\x1b[1m' + mdInline(padded) + '\x1b[0m' : mdInline(padded);
                }
                term.writeln(rendered);
            }
            tableBuffer = [];
        }

        function splitTableRow(row) {
            // Split |cell|cell| into ['cell', 'cell']
            return row.split('|').filter(function (c, i, a) {
                return i > 0 && i < a.length - 1; // skip first/last empty from leading/trailing |
            });
        }

        function renderLine(line) {
            // Check for table rows
            if (/^\|(.+\|)+\s*$/.test(line) || /^\|[\s\-:]+(\|[\s\-:]+)+\|?\s*$/.test(line)) {
                tableBuffer.push(line);
                return;
            }
            // Flush any pending table before non-table content
            flushTable();

            // Green bullet for first response text line
            if (isFirstText && line.trim().length > 0) {
                term.writeln('\x1b[32m\u25cf\x1b[0m ' + mdLine(line));
                isFirstText = false;
                return;
            }
            term.writeln(mdLine(line));
        }

        return {
            write: function (chunk) {
                lineBuffer += chunk;
                var lines = lineBuffer.split('\n');
                lineBuffer = lines.pop() || '';

                for (var i = 0; i < lines.length; i++) {
                    var line = lines[i];
                    if (line.trimStart().startsWith('```')) {
                        flushTable();
                        if (!inCodeBlock) {
                            codeBlockLang = line.trim().slice(3).trim();
                            var label = codeBlockLang || 'code';
                            term.writeln('\x1b[90m  \u250c\u2500 ' + label + ' ' + '\u2500'.repeat(Math.max(0, 50 - label.length)) + '\u2510\x1b[0m');
                            inCodeBlock = true;
                        } else {
                            term.writeln('\x1b[90m  \u2514' + '\u2500'.repeat(54) + '\u2518\x1b[0m');
                            inCodeBlock = false;
                            codeBlockLang = '';
                        }
                        continue;
                    }
                    if (inCodeBlock) {
                        term.writeln('\x1b[90m  \u2502\x1b[0m  ' + highlightCode(line, codeBlockLang));
                    } else {
                        renderLine(line);
                    }
                }
            },
            flush: function () {
                flushTable();
                if (lineBuffer) {
                    if (inCodeBlock) {
                        term.writeln('\x1b[90m  \u2502\x1b[0m  ' + highlightCode(lineBuffer, codeBlockLang));
                    } else {
                        renderLine(lineBuffer);
                    }
                    lineBuffer = '';
                }
                if (inCodeBlock) {
                    term.writeln('\x1b[90m  \u2514' + '\u2500'.repeat(54) + '\u2518\x1b[0m');
                    inCodeBlock = false;
                }
            },
            resetBullet: function () {
                isFirstText = true;
            }
        };
    }

    function highlightCode(line, lang) {
        var keywords = {
            'javascript': /\b(const|let|var|function|return|if|else|for|while|class|import|export|from|async|await|try|catch|new|this|throw|typeof|null|undefined|true|false)\b/g,
            'typescript': /\b(const|let|var|function|return|if|else|for|while|class|import|export|from|async|await|try|catch|new|this|throw|typeof|null|undefined|true|false|interface|type|enum|implements|extends|public|private|protected|readonly)\b/g,
            'csharp': /\b(public|private|protected|internal|static|void|class|interface|record|enum|struct|namespace|using|return|if|else|for|foreach|while|var|new|this|async|await|try|catch|finally|throw|null|true|false|override|virtual|abstract|sealed|readonly|string|int|bool|double|float|long|byte|object|Task)\b/g,
            'python': /\b(def|class|return|if|elif|else|for|while|import|from|try|except|finally|raise|with|as|None|True|False|self|lambda|yield|pass|break|continue|and|or|not|in|is)\b/g,
            'bash': /\b(if|then|else|fi|for|do|done|while|case|esac|echo|export|function|return|local|cd|ls|grep|sed|awk|cat|mkdir|rm|cp|mv)\b/g,
            'json': null
        };

        var kw = keywords[lang] || keywords['javascript'];
        if (lang === 'json') {
            line = line.replace(/"([^"]+)"(\s*:)/g, '\x1b[36m"$1"\x1b[0m$2');
            line = line.replace(/:\s*"([^"]*)"/g, ': \x1b[32m"$1"\x1b[0m');
            line = line.replace(/:\s*(true|false|null)\b/g, ': \x1b[33m$1\x1b[0m');
            line = line.replace(/:\s*(\d+)/g, ': \x1b[33m$1\x1b[0m');
            return line;
        }
        if (!kw) return line;
        if (/^\s*\/\//.test(line) || /^\s*#/.test(line))
            return '\x1b[90m' + line + '\x1b[0m';
        line = line.replace(/"([^"]*)"/g, '\x1b[32m"$1"\x1b[0m');
        line = line.replace(/'([^']*)'/g, "\x1b[32m'$1'\x1b[0m");
        line = line.replace(kw, '\x1b[35m$1\x1b[0m');
        return line;
    }

    // Render tool start as compact bullet (matches real Copilot CLI)
    function renderToolStart(term, name, args) {
        if (HIDDEN_TOOLS.indexOf(name) >= 0) return; // suppress meta-tools
        var desc = toolDescription(name, args);
        var type = TOOL_TYPES[name] || 'tool';
        term.writeln('\x1b[33m\u25cf\x1b[0m \x1b[1m' + desc + '\x1b[0m \x1b[90m(' + type + ')\x1b[0m');
        // Show key args indented
        if (args) {
            try {
                var parsed = (typeof args === 'string') ? JSON.parse(args) : args;
                var keys = Object.keys(parsed);
                for (var i = 0; i < keys.length && i < 3; i++) {
                    var k = keys[i];
                    if (k === 'description') continue; // already in title
                    var v = String(parsed[k]);
                    if (v.length > 80) v = v.slice(0, 77) + '...';
                    term.writeln('    \x1b[90m' + v + '\x1b[0m');
                }
            } catch (_) { }
        }
    }

    function renderToolComplete(term, name, success, output) {
        if (HIDDEN_TOOLS.indexOf(name) >= 0) return; // suppress meta-tools
        if (!output) return;

        // Parse JSON content if needed
        var displayOutput = output;
        try {
            var parsed = JSON.parse(output);
            if (parsed && typeof parsed === 'object') {
                displayOutput = parsed.content || parsed.detailedContent || output;
            }
        } catch (_) { }

        var lines = displayOutput.split('\n').filter(function (l) { return l.trim().length > 0; });
        if (lines.length === 0) return;

        if (lines.length <= 2) {
            // Short output: show inline
            for (var i = 0; i < lines.length; i++) {
                var ol = lines[i];
                if (ol.length > 80) ol = ol.slice(0, 77) + '...';
                term.writeln('    \x1b[90m' + ol + '\x1b[0m');
            }
        } else {
            // Collapsed output: show line count
            term.writeln('    \x1b[90m' + lines.length + ' lines...\x1b[0m');
        }
    }

    function createSpinner(term) {
        var frames = ['\u280b', '\u2819', '\u2839', '\u2838', '\u283c', '\u2834', '\u2826', '\u2827', '\u2807', '\u280f'];
        var idx = 0;
        var startTime = Date.now();
        var interval = null;
        var lineWritten = false;
        var label = 'Thinking';

        return {
            start: function () {
                startTime = Date.now();
                idx = 0;
                interval = setInterval(function () {
                    var elapsed = ((Date.now() - startTime) / 1000).toFixed(1);
                    var frame = frames[idx % frames.length];
                    idx++;
                    var text = '\r  \x1b[35m' + frame + '\x1b[0m \x1b[90m' + label + '... (' + elapsed + 's)\x1b[0m\x1b[K';
                    term.write(text);
                    lineWritten = true;
                }, 80);
            },
            setLabel: function (l) { label = l; },
            stop: function () {
                if (interval) { clearInterval(interval); interval = null; }
                if (lineWritten) { term.write('\r\x1b[K'); lineWritten = false; }
            }
        };
    }

    window.DirectorCLI = {
        init: function (containerId, objRef) {
            dotNetRef = objRef;
            window.addEventListener('resize', function () {
                if (activeThreadId && terminals[activeThreadId]) {
                    try { terminals[activeThreadId].fitAddon.fit(); } catch (_) { }
                }
            });
        },

        activateThread: function (threadId, containerId) {
            Object.keys(terminals).forEach(function (id) {
                if (terminals[id] && terminals[id].element) {
                    terminals[id].element.style.display = 'none';
                }
            });
            activeThreadId = threadId;

            if (terminals[threadId]) {
                terminals[threadId].element.style.display = 'block';
                try { terminals[threadId].fitAddon.fit(); } catch (_) { }
                terminals[threadId].term.focus();
                return;
            }

            var container = document.getElementById(containerId);
            if (!container) return;

            var termEl = document.createElement('div');
            termEl.id = 'term-' + threadId;
            termEl.style.height = '100%';
            termEl.style.width = '100%';
            container.appendChild(termEl);

            var term = new Terminal({
                cursorBlink: true,
                cursorStyle: 'bar',
                fontSize: 14,
                fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', 'Consolas', monospace",
                lineHeight: 1.3,
                theme: {
                    background: '#0d1117',
                    foreground: '#c9d1d9',
                    cursor: '#58a6ff',
                    cursorAccent: '#0d1117',
                    selectionBackground: 'rgba(88, 166, 255, 0.3)',
                    selectionForeground: '#f0f6fc',
                    black: '#484f58',
                    red: '#ff7b72',
                    green: '#3fb950',
                    yellow: '#d29922',
                    blue: '#58a6ff',
                    magenta: '#bc8cff',
                    cyan: '#39d0ff',
                    white: '#c9d1d9',
                    brightBlack: '#6e7681',
                    brightRed: '#ffa198',
                    brightGreen: '#56d364',
                    brightYellow: '#e3b341',
                    brightBlue: '#79c0ff',
                    brightMagenta: '#d2a8ff',
                    brightCyan: '#56d4ff',
                    brightWhite: '#f0f6fc'
                },
                scrollback: 10000,
                allowProposedApi: true
            });

            var fit = new FitAddon.FitAddon();
            term.loadAddon(fit);
            term.open(termEl);

            setTimeout(function () {
                try { fit.fit(); } catch (_) { }
            }, 100);

            var currentLine = '';
            var commandHistory = [];
            var historyIdx = -1;
            var savedLine = '';

            try {
                var saved = localStorage.getItem('director-cli-history-' + threadId);
                if (saved) commandHistory = JSON.parse(saved);
            } catch (_) { }

            term.onKey(function (ev) {
                var key = ev.key;
                var code = ev.domEvent.keyCode;

                if (code === 13) {
                    term.write('\r\n');
                    if (currentLine.trim()) {
                        commandHistory.push(currentLine.trim());
                        if (commandHistory.length > 100) commandHistory.shift();
                        try {
                            localStorage.setItem('director-cli-history-' + threadId, JSON.stringify(commandHistory));
                        } catch (_) { }
                        historyIdx = -1;
                        savedLine = '';
                        dotNetRef.invokeMethodAsync('OnCommandEntered', threadId, currentLine.trim());
                    } else {
                        writePrompt(term);
                    }
                    currentLine = '';
                } else if (code === 8) {
                    if (currentLine.length > 0) {
                        currentLine = currentLine.slice(0, -1);
                        term.write('\b \b');
                    }
                } else if (code === 38) {
                    if (commandHistory.length > 0) {
                        if (historyIdx === -1) {
                            savedLine = currentLine;
                            historyIdx = commandHistory.length - 1;
                        } else if (historyIdx > 0) {
                            historyIdx--;
                        }
                        term.write('\r\x1b[K');
                        writePromptInline(term);
                        currentLine = commandHistory[historyIdx];
                        term.write(currentLine);
                    }
                } else if (code === 40) {
                    if (historyIdx !== -1) {
                        if (historyIdx < commandHistory.length - 1) {
                            historyIdx++;
                            term.write('\r\x1b[K');
                            writePromptInline(term);
                            currentLine = commandHistory[historyIdx];
                            term.write(currentLine);
                        } else {
                            historyIdx = -1;
                            term.write('\r\x1b[K');
                            writePromptInline(term);
                            currentLine = savedLine;
                            term.write(currentLine);
                        }
                    }
                } else if (code === 3 && ev.domEvent.ctrlKey) {
                    dotNetRef.invokeMethodAsync('OnCancelRequested', threadId);
                    currentLine = '';
                    term.write('^C\r\n');
                    writePrompt(term);
                } else if (code === 76 && ev.domEvent.ctrlKey) {
                    term.clear();
                    writePrompt(term);
                    currentLine = '';
                } else if (key.length === 1 && !ev.domEvent.ctrlKey && !ev.domEvent.altKey) {
                    currentLine += key;
                    term.write(key);
                }
            });

            term.onData(function (data) {
                if (data.length > 1 && !data.startsWith('\x1b')) {
                    currentLine += data;
                    term.write(data);
                }
            });

            // Welcome message
            term.writeln('');
            term.writeln('  \x1b[1;97mGitHub Copilot\x1b[0m \x1b[90m(VirtualDevTeam Director)\x1b[0m');
            term.writeln('  \x1b[90mModel: waiting... \u00b7 Session: ' + threadId + '\x1b[0m');
            term.writeln('  \x1b[90mType a request. Ctrl+C to cancel. \u2191\u2193 for history.\x1b[0m');
            term.writeln('');
            writePrompt(term);

            terminals[threadId] = {
                term: term,
                fitAddon: fit,
                element: termEl,
                spinner: createSpinner(term),
                renderer: createStreamRenderer(term),
                promptWritten: true
            };

            term.focus();
        },

        handleEvent: function (threadId, eventJson) {
            var t = terminals[threadId];
            if (!t) return;

            var ev;
            try { ev = JSON.parse(eventJson); } catch (_) { return; }
            var term = t.term;

            switch (ev.type) {
                case 'user_command':
                    // Replay user command as prompt + text
                    writePrompt(term);
                    term.writeln(ev.content);
                    break;

                case 'thinking_start':
                    if (!t.isReplay) {
                        t.spinner.setLabel('Thinking');
                        t.spinner.start();
                    }
                    t.promptWritten = false;
                    t.renderer.resetBullet();
                    break;

                case 'text_delta':
                    t.spinner.stop();
                    t.renderer.write(ev.content);
                    break;

                case 'text_full':
                    t.spinner.stop();
                    var lines = ev.content.split('\n');
                    for (var i = 0; i < lines.length; i++) {
                        term.writeln(mdLine(lines[i]));
                    }
                    break;

                case 'text_done':
                    t.spinner.stop();
                    t.renderer.flush();
                    break;

                case 'tool_start':
                    t.spinner.stop();
                    if (HIDDEN_TOOLS.indexOf(ev.name) < 0 && !t.isReplay) {
                        t.spinner.setLabel('Running: ' + ev.name);
                    }
                    renderToolStart(term, ev.name, ev.args);
                    if (HIDDEN_TOOLS.indexOf(ev.name) < 0 && !t.isReplay) {
                        t.spinner.start();
                    }
                    t.renderer.resetBullet();
                    break;

                case 'tool_complete':
                    t.spinner.stop();
                    if (!t.isReplay) t.spinner.setLabel('Thinking');
                    renderToolComplete(term, ev.name, ev.success, ev.output);
                    break;

                case 'mcp_status':
                    if (ev.content) {
                        var parts = ev.content.split(':');
                        var server = parts[0] || '';
                        var stat = parts[1] || '';
                        if (stat === 'connected') {
                            t.spinner.setLabel('Connected: ' + server);
                        }
                    }
                    break;

                case 'model':
                    break;

                case 'result':
                    t.spinner.stop();
                    if (ev.premiumRequests > 0) {
                        term.writeln('');
                        term.writeln('  \x1b[90m\u26a1 ' + ev.premiumRequests + ' premium request' +
                            (ev.premiumRequests > 1 ? 's' : '') +
                            (ev.sessionDurationMs > 0 ? ' \u00b7 ' + (ev.sessionDurationMs / 1000).toFixed(1) + 's' : '') +
                            '\x1b[0m');
                    }
                    break;

                case 'error':
                    t.spinner.stop();
                    term.writeln('\x1b[31m\u2717 Error: ' + ev.content + '\x1b[0m');
                    break;

                case 'cancelled':
                    t.spinner.stop();
                    term.writeln('\x1b[33m\u26a0 Cancelled\x1b[0m');
                    break;

                case 'command_done':
                    t.spinner.stop();
                    t.renderer.flush();
                    term.writeln('');
                    writePrompt(term);
                    t.promptWritten = true;
                    break;
            }
        },

        setReplayMode: function (threadId, isReplay) {
            if (terminals[threadId]) {
                terminals[threadId].isReplay = isReplay;
                if (isReplay) {
                    // Clear the welcome banner before replaying history
                    terminals[threadId].term.clear();
                }
            }
        },

        writeOutput: function (threadId, text) {
            if (terminals[threadId]) {
                terminals[threadId].term.write(text);
            }
        },

        clearTerminal: function (threadId) {
            if (terminals[threadId]) terminals[threadId].term.clear();
        },

        removeThread: function (threadId) {
            if (terminals[threadId]) {
                terminals[threadId].spinner.stop();
                terminals[threadId].term.dispose();
                if (terminals[threadId].element) terminals[threadId].element.remove();
                delete terminals[threadId];
            }
        },

        fitAll: function () {
            Object.keys(terminals).forEach(function (id) {
                try { terminals[id].fitAddon.fit(); } catch (_) { }
            });
        },

        destroy: function () {
            Object.keys(terminals).forEach(function (id) {
                try {
                    terminals[id].spinner.stop();
                    terminals[id].term.dispose();
                } catch (_) { }
            });
            Object.keys(terminals).forEach(function (id) { delete terminals[id]; });
            dotNetRef = null;
            activeThreadId = null;
        }
    };

    function writePrompt(term) {
        term.write('\x1b[90m~\x1b[0m \x1b[36m\u276f\x1b[0m ');
    }

    function writePromptInline(term) {
        term.write('\x1b[90m~\x1b[0m \x1b[36m\u276f\x1b[0m ');
    }
})();
