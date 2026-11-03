namespace Dormant.Report;

/// <summary>The report's inline stylesheet and script: the file makes no requests of its own (decisions D-055, D-064).</summary>
internal static class Assets
{
    // The Control room theme (decision D-064): dark on screen, the same palette turned light for print. Every colour is a
    // variable, so print only redefines them. The heat ramp runs from the panel's own tone to near-white; amber marks
    // Dormant and nothing else.
    public const string Css = """
        :root {
          color-scheme: dark;
          --page: #0B111B; --panel: #111A27; --panel-2: #162133; --rule: #243246; --rule-strong: #34465E;
          --ink: #D9E2EC; --grey: #8FA1B5; --muted: #6B7B8F; --accent: #FFB547; --on-accent: #3A2400;
          --s0: #1E2A3B; --s1: #1F4E5F; --s2: #2A8C8C; --s3: #5FD3B8; --s4: #C8FFE9;
          --hatch-bg: #111A27; --hatch-line: #3A4658;
          --k-hot: #5FD3B8; --on-hot: #06302A; --k-warm: #2A8C8C; --on-warm: #E8FFF8; --k-cold: #3A4658; --on-cold: #D9E2EC;
        }
        * { box-sizing: border-box; }
        html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        body { margin: 0; background: var(--page); color: var(--ink);
          font: 14px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif; }
        main { max-width: 1240px; margin: 0 auto; padding: 32px 24px 64px; }
        h1 { font-size: 24px; font-weight: 600; margin: 0 0 4px; letter-spacing: -0.01em; }
        h1::before { content: ""; display: inline-block; width: 10px; height: 10px; background: var(--accent); border-radius: 2px; margin-right: 10px; vertical-align: 2px; }
        h2 { font-size: 18px; font-weight: 600; margin: 44px 0 10px; padding-top: 14px; border-top: 1px solid var(--rule-strong); }
        h3 { font-size: 15px; font-weight: 600; margin: 26px 0 6px; }
        h4 { font-size: 13px; font-weight: 600; margin: 16px 0 4px; color: var(--grey); }
        p, li { max-width: 860px; }
        a { color: var(--ink); }
        .lede { color: var(--grey); margin: 0 0 8px; }
        .muted { color: var(--grey); }
        code, .mono, td.num, .figure b { font-family: "Cascadia Mono", ui-monospace, "SF Mono", Consolas, "Liberation Mono", monospace; font-variant-numeric: tabular-nums; }
        code, .mono { font-size: 12.5px; }
        nav { margin: 16px 0 0; font-size: 13px; }
        nav a { color: var(--grey); margin-right: 16px; text-decoration: none; border-bottom: 1px solid var(--rule-strong); }
        nav a:hover { color: var(--ink); }
        table { border-collapse: collapse; width: 100%; background: var(--panel); }
        th, td { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--rule); vertical-align: top; }
        th { font-weight: 600; font-size: 12px; color: var(--grey); background: var(--panel-2); white-space: nowrap; }
        th[data-sort] { cursor: pointer; }
        th[data-dir="asc"]::after { content: " \2191"; }
        th[data-dir="desc"]::after { content: " \2193"; }
        td.num, th.num { text-align: right; white-space: nowrap; }
        td.num { font-size: 12.5px; }
        .figures { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px; margin: 16px 0; }
        .figure { background: var(--panel); border: 1px solid var(--rule); border-radius: 8px; padding: 12px 14px; }
        .figure b { display: block; font-size: 22px; font-weight: 600; }
        .figure span { color: var(--grey); font-size: 12.5px; }
        .chip { display: inline-block; padding: 0 7px; font-size: 12px; line-height: 19px; border-radius: 3px; white-space: nowrap; }
        .flag { display: inline-block; padding: 0 6px; font-size: 11.5px; line-height: 17px; border: 1px solid var(--rule-strong); color: var(--grey); border-radius: 3px; white-space: nowrap; }
        .k-hot { background: var(--k-hot); color: var(--on-hot); }
        .k-warm { background: var(--k-warm); color: var(--on-warm); }
        .k-cold { background: var(--k-cold); color: var(--on-cold); }
        .k-dormant { background: var(--accent); color: var(--on-accent); }
        .k-insufficient { color: var(--ink); background: repeating-linear-gradient(135deg, var(--hatch-bg) 0 3px, var(--hatch-line) 3px 4px); box-shadow: inset 0 0 0 1px var(--rule-strong); }
        .warning { background: var(--panel); border-left: 3px solid var(--accent); padding: 8px 12px; margin: 12px 0; max-width: 860px; }
        .controls { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin: 12px 0; font-size: 13px; color: var(--grey); }
        .controls input, .controls select { font: inherit; padding: 3px 6px; border: 1px solid var(--rule-strong); border-radius: 4px; background: var(--panel); color: var(--ink); }
        .legend { display: flex; flex-wrap: wrap; gap: 14px; align-items: center; font-size: 12px; color: var(--grey); margin: 8px 0; }
        .legend .legend-title { color: var(--ink); }
        .legend i { display: inline-block; width: 12px; height: 12px; vertical-align: -2px; margin-right: 5px; border-radius: 2px; }
        .sw.c0 { background: var(--s0); } .sw.c1 { background: var(--s1); } .sw.c2 { background: var(--s2); }
        .sw.c3 { background: var(--s3); } .sw.c4 { background: var(--s4); }
        .sw.nd { background: repeating-linear-gradient(135deg, var(--hatch-bg) 0 2px, var(--hatch-line) 2px 3px); box-shadow: inset 0 0 0 1px var(--rule-strong); }
        .sw.ab { background: transparent; box-shadow: inset 0 0 0 1px var(--rule); }
        .heat-db { overflow-x: auto; background: var(--panel); border: 1px solid var(--rule); border-radius: 8px; padding: 4px 8px 8px; margin: 8px 0 14px; }
        .heat-db h4 { margin: 8px 0 4px; }
        .heat, .heat th { background: var(--panel); }
        .heat td { padding: 2px 8px; font-size: 12.5px; border-bottom: none; }
        .heat td.cells { padding: 2px 8px 2px 0; line-height: 0; }
        .heat tr.part td.name { padding-left: 22px; color: var(--grey); }
        .heat td.name { max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .heat tbody:hover td { background: var(--panel-2); }
        .heat svg { display: block; }
        .heat svg path { shape-rendering: crispEdges; }
        path.c0 { fill: var(--s0); } path.c1 { fill: var(--s1); } path.c2 { fill: var(--s2); }
        path.c3 { fill: var(--s3); } path.c4 { fill: var(--s4); } path.nd { fill: url(#hatch); }
        .hatch-bg { fill: var(--hatch-bg); } .hatch-line { stroke: var(--hatch-line); }
        .axis text { font-size: 10px; fill: var(--muted); font-family: "Cascadia Mono", ui-monospace, Consolas, monospace; }
        .axis line { stroke: var(--rule-strong); stroke-width: 1; }
        .axis .restart { fill: var(--ink); }
        .axis .reset { fill: none; stroke: var(--ink); stroke-width: 1; }
        .strip { display: flex; height: 22px; border: 1px solid var(--rule); border-radius: 4px; overflow: hidden; background: var(--panel); margin: 4px 0; }
        .strip span { flex: 1 1 0; min-width: 2px; border-right: 1px solid var(--panel); }
        .strip-ends { display: flex; justify-content: space-between; font-size: 11.5px; color: var(--muted); }
        .strip-table { margin: 18px 0; max-width: 1100px; }
        .strip-table p { margin: 2px 0; font-size: 12.5px; color: var(--grey); }
        dl.thresholds { display: grid; grid-template-columns: max-content 1fr; gap: 2px 16px; }
        dl.thresholds dt { color: var(--grey); }
        dl.thresholds dd { margin: 0; }
        footer { margin-top: 48px; color: var(--muted); font-size: 12px; }
        @media print {
          @page { size: A4 landscape; margin: 10mm; }
          :root {
            color-scheme: light;
            --page: #FFFFFF; --panel: #FFFFFF; --panel-2: #F2F5F7; --rule: #DDE3E8; --rule-strong: #B8C2CC;
            --ink: #111A27; --grey: #4A5868; --muted: #6B7B8F; --accent: #F5A623; --on-accent: #2A1A00;
            --s0: #EEF2F5; --s1: #BFE3DA; --s2: #5FBFAA; --s3: #2A8C8C; --s4: #0F4F4F;
            --hatch-bg: #FFFFFF; --hatch-line: #A9B4BF;
            --k-hot: #0F4F4F; --on-hot: #FFFFFF; --k-warm: #2A8C8C; --on-warm: #FFFFFF; --k-cold: #DDE3E8; --on-cold: #111A27;
          }
          body { font-size: 11px; }
          main { max-width: none; padding: 0; }
          nav, .controls { display: none; }
          h2 { break-before: page; margin-top: 0; }
          h2.first { break-before: auto; }
          tr, .strip-table, .figure { break-inside: avoid; }
          tbody[hidden] { display: none; }
          .heat-db { border: none; padding: 0; }
        }
        """;

    // Sorting by a column header and filtering the heatmap; the report reads fully without it.
    public const string Script = """
        (function () {
          document.querySelectorAll('th[data-sort]').forEach(function (th) {
            th.addEventListener('click', function () {
              var table = th.closest('table'), key = th.getAttribute('data-sort');
              var numeric = key === 'size' || key === 'rank' || key === 'updates';
              var current = th.getAttribute('data-dir');
              var dir = current ? (current === 'desc' ? 'asc' : 'desc') : (numeric ? 'desc' : 'asc');
              table.querySelectorAll('th[data-sort]').forEach(function (h) { h.removeAttribute('data-dir'); });
              th.setAttribute('data-dir', dir);
              var byBody = table.tBodies.length > 1;
              var items = byBody ? Array.prototype.slice.call(table.tBodies) : Array.prototype.slice.call(table.tBodies[0].rows);
              items.sort(function (a, b) {
                var x = a.getAttribute('data-' + key) || '', y = b.getAttribute('data-' + key) || '';
                var r = numeric ? (Number(x) - Number(y)) : x.localeCompare(y);
                return dir === 'asc' ? r : -r;
              });
              var parent = byBody ? table : table.tBodies[0];
              items.forEach(function (item) { parent.appendChild(item); });
            });
          });
          var text = document.getElementById('filter-text'), cls = document.getElementById('filter-class');
          if (!text || !cls) { return; }
          function apply() {
            var q = text.value.trim().toLowerCase(), k = cls.value;
            document.querySelectorAll('table.heat').forEach(function (table) {
              var shown = 0;
              Array.prototype.forEach.call(table.tBodies, function (body) {
                var match = (!q || body.getAttribute('data-name').indexOf(q) >= 0) && (!k || body.getAttribute('data-classes').indexOf(k) >= 0);
                body.hidden = !match;
                if (match) { shown++; }
              });
              table.closest('.heat-db').hidden = shown === 0;
            });
          }
          text.addEventListener('input', apply);
          cls.addEventListener('change', apply);
        })();
        """;
}
