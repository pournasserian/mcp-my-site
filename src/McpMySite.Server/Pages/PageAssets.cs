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
          color-scheme: light dark;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0c111b; --surface: #141b27; --text: #e6eaf0; --muted: #9aa5b5; --border: #263042;
            --accent: #7ea6ff; --accent-soft: #1b2740; --code-bg: #1b2333; --warn-bg: #3a3014; --warn-border: #8a6d1f;
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
}
