namespace McpMySite.Server.Pages;

/// <summary>
/// Styles and script inlined into every page. Inline keeps a page to one request and needs no static file
/// pipeline; the pages are small enough that caching them separately would not pay off.
/// </summary>
internal static class PageAssets
{
    public const string Css = """
        :root {
          --bg: #f7f8fa; --surface: #ffffff; --text: #18202c; --muted: #5b6676; --border: #dde2e9;
          --accent: #2456c7; --accent-soft: #e8eefb; --code-bg: #f0f2f5; --warn-bg: #fff5d6; --warn-border: #e6c25a;
          --on-accent: #ffffff; --error: #b42318;
          color-scheme: light dark;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0c111b; --surface: #141b27; --text: #e6eaf0; --muted: #9aa5b5; --border: #263042;
            --accent: #7ea6ff; --accent-soft: #1b2740; --code-bg: #1b2333; --warn-bg: #3a3014; --warn-border: #8a6d1f;
            --on-accent: #0c111b; --error: #ff8a80;
          }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--text); font: 16px/1.6 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        a { color: var(--accent); }
        header.hero, main, footer { max-width: 56rem; margin: 0 auto; padding: 0 1rem; }
        header.hero { padding-top: 3rem; padding-bottom: 1rem; }
        .eyebrow { margin: 0; color: var(--muted); font-size: .85rem; letter-spacing: .08em; text-transform: uppercase; }
        h1 { margin: .25rem 0 .5rem; font-size: clamp(1.8rem, 4vw, 2.4rem); line-height: 1.2; }
        h2 { margin: 0 0 .75rem; font-size: 1.3rem; }
        h3 { margin: 1.25rem 0 .5rem; font-size: 1.05rem; }
        .lead { margin: 0 0 .5rem; font-size: 1.15rem; color: var(--muted); }
        nav.links { display: flex; flex-wrap: wrap; gap: .5rem 1.25rem; margin-top: 1rem; }
        section { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 1.25rem 1.5rem; margin: 1rem 0; }
        .notice { background: var(--warn-bg); border: 1px solid var(--warn-border); border-radius: 8px; padding: .6rem .9rem; }
        code, pre { font: .9rem/1.5 ui-monospace, SFMono-Regular, Consolas, monospace; }
        code { background: var(--code-bg); border-radius: 4px; padding: .1rem .3rem; }
        pre { background: var(--code-bg); border-radius: 8px; padding: .75rem 1rem; overflow-x: auto; margin: .5rem 0; }
        pre code { background: none; padding: 0; }
        .copyable { display: flex; align-items: center; gap: .5rem; flex-wrap: wrap; }
        .copyable code { font-size: 1rem; padding: .4rem .6rem; overflow-wrap: anywhere; }
        button.copy { display: none; font: inherit; font-size: .85rem; padding: .3rem .7rem; border-radius: 6px; border: 1px solid var(--border); background: var(--accent-soft); color: var(--text); cursor: pointer; }
        .js button.copy { display: inline-block; }
        details { border-top: 1px solid var(--border); padding: .5rem 0; }
        details:last-of-type { border-bottom: 1px solid var(--border); }
        summary { cursor: pointer; font-weight: 600; }
        dl.tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(8rem, 1fr)); gap: .75rem; margin: 0; }
        dl.tiles div { background: var(--accent-soft); border-radius: 8px; padding: .6rem .8rem; }
        dl.tiles dt { color: var(--muted); font-size: .85rem; }
        dl.tiles dd { margin: 0; font-size: 1.25rem; font-weight: 600; }
        dl.tiles dd small { display: block; font-size: .8rem; font-weight: 400; color: var(--muted); }
        table { width: 100%; border-collapse: collapse; margin: .5rem 0; }
        th, td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid var(--border); vertical-align: top; }
        td.num, th.num { text-align: right; white-space: nowrap; }
        dl.facts { display: grid; grid-template-columns: max-content 1fr; gap: .3rem 1rem; margin: 0; }
        dl.facts dt { color: var(--muted); }
        dl.facts dd { margin: 0; }
        footer { color: var(--muted); font-size: .9rem; padding-bottom: 2rem; }
        .actions { display: flex; align-items: center; flex-wrap: wrap; gap: .5rem 1rem; }
        .field { display: grid; gap: .25rem; margin: .6rem 0; }
        .field-name { font-weight: 600; font-size: .9rem; }
        .field small { color: var(--muted); }
        input[type=text], input[type=password], input[type=number], textarea, select { font: inherit; width: 100%; padding: .45rem .6rem; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
        input[type=checkbox] { justify-self: start; width: 1.1rem; height: 1.1rem; }
        button.primary, button.secondary { font: inherit; padding: .45rem 1rem; border-radius: 6px; cursor: pointer; }
        button.primary { font-weight: 600; border: 0; background: var(--accent); color: var(--on-accent); }
        button.secondary { border: 1px solid var(--border); background: var(--accent-soft); color: var(--text); }
        button:disabled { opacity: .6; cursor: progress; }
        .error { color: var(--error); }
        .tool { border-top: 1px solid var(--border); padding: 1rem 0 .5rem; }
        .tool h3 { margin: 0 0 .25rem; }
        .tool > p { margin: 0 0 .5rem; color: var(--muted); }
        .result { margin-top: .75rem; }
        ol.hits { list-style: none; padding: 0; margin: .5rem 0; display: grid; gap: .6rem; }
        ol.hits li { border: 1px solid var(--border); border-radius: 8px; padding: .6rem .8rem; }
        ol.hits p { margin: .25rem 0; }
        .meta { color: var(--muted); font-size: .85rem; }
        pre.page { white-space: pre-wrap; overflow-wrap: anywhere; max-height: 32rem; overflow-y: auto; }
        ol.log { list-style: none; padding: 0; margin: 0; }
        ol.log li { border-top: 1px solid var(--border); padding: .35rem 0; }
        ol.log summary { font: .85rem/1.5 ui-monospace, SFMono-Regular, Consolas, monospace; font-weight: 400; }
        @media (max-width: 40rem) { section { padding: 1rem; } dl.facts { grid-template-columns: 1fr; } dl.facts dd { margin-bottom: .4rem; } }
        """;

    /// <summary>Copy buttons only; the pages work without script.</summary>
    public const string Script = """
        document.documentElement.classList.add('js');
        document.addEventListener('click', async (event) => {
          const button = event.target.closest('button[data-copy]');
          if (!button) return;
          const source = document.getElementById(button.dataset.copy);
          try {
            await navigator.clipboard.writeText(source ? source.innerText.trim() : '');
            button.textContent = 'Copied';
          } catch {
            button.textContent = 'Copy failed';
          }
          setTimeout(() => { button.textContent = 'Copy'; }, 1500);
        });
        """;

    /// <summary>
    /// The playground's MCP client. Everything from the server is written with textContent, never as HTML, because
    /// titles, snippets and pages come from the crawled site.
    /// </summary>
    public const string PlaygroundScript = """
        (() => {
          const root = document.getElementById('playground');
          const endpoint = root.dataset.endpoint;
          const requiresKey = root.dataset.requiresKey === 'true';
          const keyInput = document.getElementById('api-key');
          const connectButton = document.getElementById('connect');
          const status = document.getElementById('status');
          const toolsBox = document.getElementById('tools');
          const log = document.getElementById('log');
          const maxLogEntries = 50;
          let nextId = 0;
          let protocolVersion = null;
          let sessionId = null;

          const el = (tag, props = {}, ...children) => {
            const node = Object.assign(document.createElement(tag), props);
            node.append(...children.filter(c => c !== null && c !== undefined));
            return node;
          };
          const json = value => JSON.stringify(value, null, 2);
          const safeHref = url => {
            try {
              const parsed = new URL(url);
              return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? parsed.href : null;
            } catch { return null; }
          };
          const link = (text, url) => {
            const href = safeHref(url);
            return href ? el('a', { href, textContent: text, target: '_blank', rel: 'noopener' }) : el('span', { textContent: text });
          };
          const setStatus = (text, isError = false) => {
            status.textContent = text;
            status.className = isError ? 'error' : '';
          };

          function addLog(request, response, note, ms) {
            document.getElementById('log-section').hidden = false;
            const label = request.method === 'tools/call' ? `tools/call ${request.params.name}` : request.method;
            const outcome = response?.error ? 'error' : response?.result?.isError ? 'tool error' : note ?? 'ok';
            const entry = el('li', {}, el('details', {},
              el('summary', { textContent: `${new Date().toLocaleTimeString()}  ${label}  ${ms} ms  ${outcome}` }),
              el('p', { className: 'meta', textContent: 'Request' }), el('pre', { textContent: json(request) }),
              el('p', { className: 'meta', textContent: 'Response' }), el('pre', { textContent: response ? json(response) : note ?? '(none)' })));
            log.prepend(entry);
            while (log.children.length > maxLogEntries) log.lastElementChild.remove();
          }

          // Streamable HTTP answers with plain JSON or with server-sent events; take the event that answers this request.
          function parseResponse(text, contentType, id) {
            if (!text) return null;
            if (!contentType.includes('text/event-stream')) return JSON.parse(text);
            const messages = text.split(/\r?\n\r?\n/)
              .map(block => block.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n'))
              .filter(data => data.length > 0)
              .map(data => JSON.parse(data));
            return messages.find(m => m.id === id) ?? messages.at(-1) ?? null;
          }

          async function rpc(method, params, notification = false) {
            const request = { jsonrpc: '2.0', method };
            if (!notification) request.id = ++nextId;
            if (params !== undefined) request.params = params;
            const headers = { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream' };
            if (protocolVersion) headers['MCP-Protocol-Version'] = protocolVersion;
            if (sessionId) headers['Mcp-Session-Id'] = sessionId;
            const key = keyInput?.value.trim();
            if (key) headers['Authorization'] = `Bearer ${key}`;

            const started = performance.now();
            let response, text;
            try {
              response = await fetch(endpoint, { method: 'POST', headers, body: JSON.stringify(request) });
              text = await response.text();
            } catch (error) {
              addLog(request, null, `network error: ${error.message}`, Math.round(performance.now() - started));
              throw new Error(`Could not reach the server: ${error.message}`);
            }
            const ms = Math.round(performance.now() - started);
            sessionId = response.headers.get('Mcp-Session-Id') ?? sessionId;

            let message = null;
            try { message = notification ? null : parseResponse(text, response.headers.get('Content-Type') ?? '', request.id); } catch { }
            addLog(request, message, message ? null : `HTTP ${response.status} ${text.slice(0, 500)}`.trim(), ms);

            if (response.status === 401) throw new Error('The server needs a valid API key.');
            if (response.status === 429) throw new Error('Too many requests. Wait a minute and try again.');
            if (notification) return null;
            if (!message) throw new Error(`Unexpected response (HTTP ${response.status}).`);
            if (message.error) throw new Error(message.error.message ?? 'The server returned an error.');
            return message.result;
          }

          async function connect() {
            connectButton.disabled = true;
            setStatus('Connecting…');
            try {
              const init = await rpc('initialize', {
                protocolVersion: '2025-11-25',
                capabilities: {},
                clientInfo: { name: 'mcp-my-site playground', version: '1.0.0' },
              });
              protocolVersion = init.protocolVersion;
              await rpc('notifications/initialized', undefined, true);
              showServer(init);
              const list = await rpc('tools/list', {});
              showTools(list.tools ?? []);
              setStatus(`Connected. ${list.tools?.length ?? 0} tools.`);
              connectButton.textContent = 'Reconnect';
            } catch (error) {
              setStatus(error.message, true);
            } finally {
              connectButton.disabled = false;
            }
          }

          function showServer(init) {
            const facts = document.getElementById('server-facts');
            facts.replaceChildren();
            const add = (term, value) => facts.append(el('dt', { textContent: term }), el('dd', { textContent: value }));
            add('Server', `${init.serverInfo?.name ?? '?'} ${init.serverInfo?.version ?? ''}`.trim());
            add('Protocol', init.protocolVersion ?? '?');
            add('Capabilities', Object.keys(init.capabilities ?? {}).join(', ') || 'none');
            document.getElementById('instructions').textContent = init.instructions ?? '(none)';
            document.getElementById('server-info').hidden = false;
          }

          function schemaType(schema) {
            const type = Array.isArray(schema.type) ? schema.type.find(t => t !== 'null') : schema.type;
            return type ?? 'string';
          }

          function field(name, schema, required) {
            const type = schemaType(schema);
            let input;
            if (Array.isArray(schema.enum)) {
              input = el('select');
              if (!required) input.append(el('option', { value: '', textContent: '' }));
              for (const value of schema.enum) input.append(el('option', { value: String(value), textContent: String(value) }));
            } else if (type === 'boolean') {
              input = el('input', { type: 'checkbox', checked: schema.default === true });
            } else if (type === 'integer' || type === 'number') {
              input = el('input', { type: 'number', step: type === 'integer' ? '1' : 'any' });
              if (schema.minimum !== undefined) input.min = schema.minimum;
              if (schema.maximum !== undefined) input.max = schema.maximum;
            } else if (type === 'object' || type === 'array') {
              input = el('textarea', { rows: 3, placeholder: 'JSON' });
            } else {
              input = el('input', { type: 'text' });
            }
            if (type !== 'boolean' && schema.default !== undefined && schema.default !== null) {
              input.value = typeof schema.default === 'object' ? JSON.stringify(schema.default) : String(schema.default);
            }
            input.name = name;
            input.dataset.type = type;
            input.required = required;
            return el('label', { className: 'field' },
              el('span', { className: 'field-name', textContent: required ? `${name} (required)` : name }),
              input,
              schema.description ? el('small', { textContent: schema.description }) : null);
          }

          function readArguments(form) {
            const args = {};
            for (const input of form.querySelectorAll('[name]')) {
              const type = input.dataset.type;
              if (type === 'boolean') { args[input.name] = input.checked; continue; }
              const raw = input.value.trim();
              if (raw === '') continue;
              if (type === 'integer' || type === 'number') args[input.name] = Number(raw);
              else if (type === 'object' || type === 'array') args[input.name] = JSON.parse(raw);
              else args[input.name] = raw;
            }
            return args;
          }

          function showTools(tools) {
            toolsBox.replaceChildren();
            for (const tool of tools) {
              const schema = tool.inputSchema ?? {};
              const required = new Set(schema.required ?? []);
              const result = el('div', { className: 'result', role: 'region', ariaLive: 'polite' });
              const button = el('button', { type: 'submit', className: 'primary', textContent: `Call ${tool.name}` });
              const form = el('form', { className: 'tool' },
                el('h3', {}, el('code', { textContent: tool.name }), tool.title ? ` ${tool.title}` : null),
                tool.description ? el('p', { textContent: tool.description }) : null,
                ...Object.entries(schema.properties ?? {}).map(([name, property]) => field(name, property, required.has(name))),
                el('p', { className: 'actions' }, button),
                result);
              form.dataset.tool = tool.name;
              form.addEventListener('submit', async event => {
                event.preventDefault();
                let args;
                try { args = readArguments(form); } catch (error) { result.replaceChildren(el('p', { className: 'error', textContent: `Invalid JSON: ${error.message}` })); return; }
                button.disabled = true;
                result.replaceChildren(el('p', { className: 'meta', textContent: 'Calling…' }));
                try {
                  showResult(result, await rpc('tools/call', { name: tool.name, arguments: args }));
                } catch (error) {
                  result.replaceChildren(el('p', { className: 'error', textContent: error.message }));
                } finally {
                  button.disabled = false;
                }
              });
              toolsBox.append(form);
            }
            document.getElementById('tools-section').hidden = false;
          }

          function fetchPage(id) {
            const form = toolsBox.querySelector('form[data-tool="fetch"]');
            const input = form?.querySelector('[name="id"]') ?? form?.querySelector('[name="url"]');
            if (!form || !input) return;
            input.value = id;
            form.scrollIntoView({ behavior: 'smooth', block: 'start' });
            form.requestSubmit();
          }

          function showResult(box, result) {
            const raw = el('details', {}, el('summary', { textContent: 'Raw result' }), el('pre', { textContent: json(result) }));
            const data = result.structuredContent;
            if (result.isError) {
              const message = (result.content ?? []).filter(c => c.type === 'text').map(c => c.text).join('\n') || 'The tool reported an error.';
              box.replaceChildren(el('p', { className: 'error', textContent: message }), raw);
            } else if (Array.isArray(data?.results)) {
              const hits = el('ol', { className: 'hits' });
              for (const hit of data.results) {
                const id = hit.id ?? hit.url;
                const fetchButton = id ? el('button', { type: 'button', className: 'secondary', textContent: 'Fetch this page' }) : null;
                fetchButton?.addEventListener('click', () => fetchPage(id));
                hits.append(el('li', {},
                  el('strong', {}, link(hit.title ?? hit.url ?? id, hit.url)),
                  hit.section ? el('p', { className: 'meta', textContent: hit.section }) : null,
                  hit.snippet ? el('p', { textContent: hit.snippet }) : null,
                  el('p', { className: 'actions' },
                    el('span', { className: 'meta', textContent: typeof hit.score === 'number' ? `score ${hit.score.toFixed(4)}` : '' }),
                    fetchButton)));
              }
              const summary = `${data.results.length} result${data.results.length === 1 ? '' : 's'}${data.keywordOnly ? ' (keyword search only)' : ''}`;
              box.replaceChildren(el('p', { className: 'meta', textContent: summary }), hits, raw);
            } else if (typeof data?.text === 'string') {
              const meta = [data.language, data.modified ? `modified ${data.modified.slice(0, 10)}` : null, `${data.text.length.toLocaleString()} characters`].filter(Boolean).join(' · ');
              box.replaceChildren(
                el('p', {}, el('strong', {}, link(data.title ?? data.url, data.url))),
                el('p', { className: 'meta', textContent: meta }),
                el('pre', { className: 'page', textContent: data.text }),
                raw);
            } else {
              const text = (result.content ?? []).filter(c => c.type === 'text').map(c => c.text).join('\n');
              box.replaceChildren(el('pre', { className: 'page', textContent: data ? json(data) : text || '(empty result)' }), raw);
            }
          }

          connectButton.addEventListener('click', connect);
          document.getElementById('clear-log').addEventListener('click', () => log.replaceChildren());
          keyInput?.addEventListener('keydown', event => { if (event.key === 'Enter') connect(); });
          if (!requiresKey) connect();
        })();
        """;
}
