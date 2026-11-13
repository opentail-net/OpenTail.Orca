namespace OpenTail.Orca.Admin;

/// <summary>
/// The review page. All dynamic text is written with textContent (never innerHTML), the token lives in
/// sessionStorage only, and the CSP allows only this page's nonce-tagged script and style.
/// </summary>
internal static class AdminPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>OpenTail approvals</title>
        <style nonce="{{nonce}}">
          :root { color-scheme: light dark; --bg:#fff; --fg:#1b1f24; --muted:#59636e; --line:#d0d7de; --accent:#0969da; --warn:#9a6700; --bad:#cf222e; --good:#1a7f37; --card:#f6f8fa; }
          @media (prefers-color-scheme: dark) { :root { --bg:#0d1117; --fg:#e6edf3; --muted:#8b949e; --line:#30363d; --accent:#4493f8; --warn:#d29922; --bad:#f85149; --good:#3fb950; --card:#161b22; } }
          * { box-sizing: border-box; }
          body { margin:0; font:15px/1.5 system-ui, sans-serif; background:var(--bg); color:var(--fg); }
          header { padding:12px 16px; border-bottom:1px solid var(--line); display:flex; gap:12px; align-items:center; flex-wrap:wrap; }
          h1 { font-size:17px; margin:0; }
          main { display:grid; grid-template-columns:minmax(260px,380px) 1fr; min-height:calc(100vh - 56px); }
          @media (max-width:760px) { main { grid-template-columns:1fr; } }
          #list { border-right:1px solid var(--line); overflow:auto; }
          .row { padding:10px 16px; border-bottom:1px solid var(--line); cursor:pointer; }
          .row:hover, .row.sel { background:var(--card); }
          .row b { display:block; } .row small { color:var(--muted); }
          #detail { padding:16px; overflow:auto; }
          .tag { display:inline-block; padding:0 8px; border:1px solid var(--line); border-radius:10px; font-size:12px; margin-right:4px; }
          .tag.warn { color:var(--warn); border-color:var(--warn); } .tag.bad { color:var(--bad); border-color:var(--bad); } .tag.good { color:var(--good); border-color:var(--good); }
          pre { background:var(--card); border:1px solid var(--line); padding:10px; overflow:auto; max-height:260px; white-space:pre-wrap; word-break:break-word; }
          textarea, input { width:100%; font:inherit; padding:6px; background:var(--bg); color:var(--fg); border:1px solid var(--line); border-radius:6px; }
          button { font:inherit; padding:6px 14px; border:1px solid var(--line); border-radius:6px; background:var(--card); color:var(--fg); cursor:pointer; }
          button.primary { background:var(--accent); color:#fff; border-color:var(--accent); } button.danger { color:var(--bad); }
          .actions { display:flex; gap:8px; flex-wrap:wrap; margin:12px 0; }
          .muted { color:var(--muted); } .err { color:var(--bad); }
          .why { border-left:4px solid var(--warn); background:var(--card); padding:8px 12px; margin:12px 0; }
          .why.rule, .why.policy { border-left-color:var(--accent); }
          .effect { font-weight:600; }
          ul.trail { list-style:none; padding:0; margin:0; } ul.trail li { padding:4px 0; border-bottom:1px solid var(--line); }
          ul.trail li.src { color:var(--warn); font-weight:600; }
          table { border-collapse:collapse; width:100%; font-size:14px; margin:6px 0 12px; } th, td { text-align:left; padding:4px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
          th { color:var(--muted); font-weight:600; } td.num { text-align:right; font-variant-numeric:tabular-nums; }
          .tabs { display:flex; gap:6px; flex-wrap:wrap; margin:8px 0 12px; } .tabs button.on { border-color:var(--accent); color:var(--accent); }
          .wf { position:relative; height:18px; margin:2px 0; background:var(--card); border-radius:3px; }
          .wf span { position:absolute; top:2px; height:14px; border-radius:3px; background:var(--accent); min-width:2px; }
          .wf span.bad { background:var(--bad); }
          .wfl { display:grid; grid-template-columns:minmax(140px,30%) 1fr 80px; gap:8px; align-items:center; font-size:13px; }
          a.link { color:var(--accent); cursor:pointer; text-decoration:underline; }
          kbd { border:1px solid var(--line); border-radius:4px; padding:0 4px; font-size:12px; }
        </style>
        </head>
        <body>
        <header>
          <h1>OpenTail approvals</h1>
          <input id="token" type="password" placeholder="Admin token" autocomplete="off" style="max-width:260px">
          <button id="connect" class="primary">Connect</button>
          <select id="filter" aria-label="Status filter"><option value="Pending">Pending</option><option value="">All</option><option value="Failed">Failed</option><option value="Succeeded">Succeeded</option><option value="Rejected">Rejected</option></select>
          <select id="quick" aria-label="Show"><option value="">Everything in this list</option><option value="mine">Only what I can decide</option><option value="high">Only high impact</option><option value="expiring">Expiring within 2 hours</option></select>
          <button id="modules">Modules</button>
          <button id="admin" hidden>Admin</button>
          <span class="muted" title="Keyboard"><kbd>j</kbd>/<kbd>k</kbd> move · <kbd>a</kbd> approve · <kbd>x</kbd> reject</span>
          <span id="msg" class="muted"></span>
        </header>
        <main><div id="list"></div><div id="detail"><p class="muted">Select an action.</p></div></main>
        <script nonce="{{nonce}}">
        (() => {
          const $ = (id) => document.getElementById(id);
          const tokenKey = 'opentail-admin-token';
          let current = null;
          let shown = [];
          let lastDetail = null;
          const high = (risk) => risk === 'ExternalEffect' || risk === 'Destructive';
          const ago = (t) => { const m = Math.round((Date.now() - new Date(t)) / 60000); return m < 1 ? 'just now' : m < 60 ? m + ' min ago' : Math.round(m / 60) + ' h ago'; };
          const left = (t) => { const m = Math.round((new Date(t) - Date.now()) / 60000); return m <= 0 ? 'expired' : m < 60 ? 'expires in ' + m + ' min' : 'expires in ' + Math.round(m / 60) + ' h'; };
          const el = (tag, text, cls) => { const e = document.createElement(tag); if (text !== undefined && text !== null) e.textContent = text; if (cls) e.className = cls; return e; };
          const say = (t, bad) => { const m = $('msg'); m.textContent = t || ''; m.className = bad ? 'err' : 'muted'; };
          async function api(path, opts = {}) {
            const r = await fetch(path, { ...opts, headers: { 'Authorization': 'Bearer ' + sessionStorage.getItem(tokenKey), 'Content-Type': 'application/json' } });
            const body = await r.json().catch(() => ({}));
            if (r.status === 401) { sessionStorage.removeItem(tokenKey); throw new Error('Your sign-in has expired or is not valid; sign in again.'); }
            if (!r.ok) throw new Error(body.error || ('HTTP ' + r.status));
            return body;
          }
          async function loadList() {
            const status = $('filter').value;
            try {
              let rows = await api('/api/actions' + (status ? '?status=' + encodeURIComponent(status) : ''));
              const quick = $('quick').value;
              if (quick === 'mine') rows = rows.filter(a => a.canDecide === true);
              if (quick === 'high') rows = rows.filter(a => high(a.risk));
              if (quick === 'expiring') rows = rows.filter(a => a.expiresAt && new Date(a.expiresAt) - Date.now() < 2 * 3600000);
              shown = rows;
              const list = $('list'); list.replaceChildren();
              if (!rows.length) list.append(el('p', 'Nothing here.', 'muted'));
              for (const a of rows) {
                const row = el('div', null, 'row' + (current === a.id ? ' sel' : ''));
                row.append(el('b', a.tool));
                row.append(el('small', (a.previewSummary || '(no preview)') + ' · ' + a.status + (a.requiredApprovals > 1 ? ' · ' + a.approvals + '/' + a.requiredApprovals : '') + (a.canDecide === false && a.status === 'Pending' ? ' · not yours to decide' : '')));
                const tags = el('div');
                if (high(a.risk)) tags.append(el('span', a.risk === 'Destructive' ? 'cannot be undone' : 'reaches other people', 'tag bad'));
                if (a.tainted) tags.append(el('span', 'after outside content', 'tag warn'));
                if (a.expiresAt && a.status === 'Pending') tags.append(el('span', left(a.expiresAt), 'tag'));
                if (a.previewUnavailable) tags.append(el('span', 'no preview', 'tag bad'));
                row.append(tags);
                row.addEventListener('click', () => show(a.id));
                list.append(row);
              }
              say(rows.length + ' shown');
            } catch (e) { say(e.message, true); }
          }
          async function show(id) {
            current = id;
            try {
              render(await api('/api/actions/' + encodeURIComponent(id))); loadList();
              if (window.matchMedia('(max-width:760px)').matches) $('detail').scrollIntoView();
            } catch (e) { say(e.message, true); }
          }
          function render(a) {
            lastDetail = a;
            const d = $('detail'); d.replaceChildren();
            d.append(el('h2', a.tool));
            const tags = el('div');
            tags.append(el('span', a.status, 'tag'));
            if (a.tainted) tags.append(el('span', 'session read untrusted content', 'tag warn'));
            if (a.previewUnavailable) tags.append(el('span', 'no preview available: check the arguments yourself', 'tag bad'));
            if (a.requiredApprovals > 1) tags.append(el('span', a.approvals + ' of ' + a.requiredApprovals + ' approvals', 'tag'));
            if (a.reviewerGroup) tags.append(el('span', 'group: ' + a.reviewerGroup, 'tag'));
            if (a.policyRule) tags.append(el('span', 'rule: ' + a.policyRule, 'tag'));
            d.append(tags);
            d.append(el('p', 'Requester ' + a.requester + (a.sessionId ? ' · session ' + a.sessionId : ' · no session') + ' · module ' + a.module + (a.moduleVersion ? ' ' + a.moduleVersion : '') + ' · asked ' + ago(a.createdAt), 'muted'));
            if (a.why && a.status === 'Pending') {
              const w = el('div', null, 'why ' + a.why.kind);
              w.append(el('b', 'Why it is waiting'));
              w.append(el('p', a.why.text));
              if (a.why.taintSource) {
                const b = el('button', 'Open what it read: ' + a.why.taintSource.tool);
                b.addEventListener('click', () => show(a.why.taintSource.actionId));
                w.append(b);
              }
              d.append(w);
            }
            // Where the data in this action may have come from (data labels): coarse, by design, and only a hint for the reviewer.
            if (a.sessionLabels && a.sessionLabels.length) d.append(el('p', 'This session has read: ' + a.sessionLabels.join(', '), 'why'));
            // Execute with an undo window: it runs at runAt unless someone stops it. Approve runs it now; Stop needs no reason.
            if (a.status === 'Pending' && a.runAt) {
              const w = el('div', null, 'why');
              w.append(el('b', 'Going out soon'));
              w.append(el('p', 'Runs at ' + new Date(a.runAt).toLocaleString() + ' unless someone stops it. Approve runs it now.'));
              const stop = el('button', 'Stop', 'danger');
              stop.addEventListener('click', async () => {
                try { lastDetail = null; render(await api('/api/actions/' + encodeURIComponent(a.id) + '/stop', { method: 'POST', body: '{}' })); say('Stopped'); loadList(); }
                catch (e) { say(e.message, true); }
              });
              w.append(stop);
              d.append(w);
            }
            d.append(el('h3', 'What will happen'));
            if (a.effect) d.append(el('p', a.effect, 'effect'));
            d.append(el('p', a.previewSummary || 'No preview was produced.'));
            if (a.preview) d.append(el('pre', JSON.stringify(a.preview, null, 2)));
            d.append(el('h3', 'Arguments as submitted'));
            d.append(el('pre', JSON.stringify(a.arguments, null, 2)));
            if (a.error) d.append(el('p', 'Error ' + a.error.code + ': ' + a.error.message, 'err'));
            // How it was decided: every check in order, as recorded when the decision was made (audit D1).
            if (a.decisionTrace && a.decisionTrace.length) {
              d.append(el('h3', 'How this was decided'));
              const ul = el('ul', null, 'trail');
              for (const t of a.decisionTrace) {
                const li = el('li', null, t.changed ? 'src' : '');
                li.append(el('span', t.check + ': ' + t.result + (t.evidence ? ' · ' + t.evidence : '')));
                if (t.changed) li.append(el('span', ' changed the outcome', 'tag warn'));
                ul.append(li);
              }
              d.append(ul);
              if (a.policyHash) d.append(el('p', 'Policy ' + a.policyHash.substring(0, 19) + '…', 'muted'));
            }
            if (a.recent && a.recent.length) {
              d.append(el('h3', 'Before this, the caller'));
              const ul = el('ul', null, 'trail');
              for (const x of a.recent) {
                const li = el('li', null, x.taintSource ? 'src' : '');
                li.append(el('span', ago(x.at) + ' · ' + x.tool + ' · ' + x.status + (x.summary ? ' · ' + x.summary : '')));
                if (x.readsUntrusted) li.append(el('span', ' read outside content', 'tag warn'));
                if (x.taintSource) li.append(el('span', ' this is why it is held', 'tag warn'));
                li.style.cursor = 'pointer'; li.addEventListener('click', () => show(x.id));
                ul.append(li);
              }
              d.append(ul);
            }
            if (a.similar && a.similar.length) {
              d.append(el('h3', 'Earlier decisions on ' + a.tool));
              for (const x of a.similar) d.append(el('p', ago(x.at) + ' · ' + x.reviewer + ' → ' + x.decision + (x.reason ? ': ' + x.reason : '') + (x.summary ? ' · ' + x.summary : ''), 'muted'));
            }
            if (a.reviews.length) {
              d.append(el('h3', 'Reviews'));
              for (const r of a.reviews) d.append(el('p', r.reviewer + ' → ' + r.decision + (r.reason ? ': ' + r.reason : '') + (r.edited ? ' (edited arguments)' : '')));
            }
            const open = a.status === 'Pending' || (a.status === 'Failed' && a.error && a.error.retryable);
            const canDecide = open && a.canDecide !== false;
            if (open && !canDecide) d.append(el('p', a.cannotDecideReason || 'You cannot decide this action.', 'muted'));
            if (canDecide) {
              const reason = el('input'); reason.placeholder = 'Reason (required to reject or request changes)';
              const edit = el('textarea'); edit.rows = 6; edit.placeholder = 'Optional: edited arguments as a JSON object, applied when you approve';
              const bar = el('div', null, 'actions');
              reason.id = 'reason';
              const mk = (label, cls, decision) => { const b = el('button', label, cls); b.addEventListener('click', () => decide(a.id, decision, reason.value, edit.value)); return b; };
              if (a.status === 'Pending') { bar.append(mk('Approve', 'primary', 'approve'), mk('Request changes', '', 'request_changes'), mk('Reject', 'danger', 'reject')); }
              else bar.append(mk('Retry', 'primary', 'retry'));
              d.append(el('h3', 'Decision')); d.append(reason); d.append(el('p')); d.append(edit); d.append(bar);
            }
            if (a.audit.length) {
              d.append(el('h3', 'Audit'));
              d.append(el('pre', a.audit.map(e => e.at + '  ' + e.event + '  ' + (e.actor || '') + '  ' + (e.detail || '')).join('\n')));
            }
          }
          async function decide(id, decision, reason, edited) {
            if ((decision === 'reject' || decision === 'request_changes') && !reason.trim()) { say('Give a reason: the caller and the audit trail see it.', true); $('reason') && $('reason').focus(); return; }
            const payload = { decision, reason: reason || null };
            if (edited.trim()) { try { payload.editedArguments = JSON.parse(edited); } catch { say('Edited arguments are not valid JSON.', true); return; } }
            try { lastDetail = null; render(await api('/api/actions/' + encodeURIComponent(id) + '/review', { method: 'POST', body: JSON.stringify(payload) })); say('Done'); loadList(); }
            catch (e) { say(e.message, true); }
          }
          async function showModules() {
            current = null;
            try {
              const r = await api('/api/modules');
              const d = $('detail'); d.replaceChildren();
              d.append(el('h2', 'Modules'));
              d.append(el('p', 'What this Host loaded. Descriptions and notes are written by each module\'s author; the policy decides what runs.', 'muted'));
              for (const m of r.modules) {
                d.append(el('h3', (m.displayName || m.id) + ' (' + m.id + ')'));
                d.append(el('p', [m.type ? 'type ' + m.type : 'added in code', m.assembly + ' ' + m.version, 'contract ' + m.contract, m.publisher ? 'by ' + m.publisher : null].filter(x => x).join(' · '), 'muted'));
                if (m.description) d.append(el('p', m.description));
                if (m.networkHosts.length) d.append(el('p', 'Talks to: ' + m.networkHosts.join(', ')));
                if (m.credentials.length) d.append(el('p', 'Credentials: ' + m.credentials.join(', ')));
                for (const n of m.notes) d.append(el('p', '• ' + n));
                d.append(el('pre', m.tools.map(t => t.name.padEnd(36) + t.risk.padEnd(16) + t.mode + (t.readsUntrusted ? '  reads outside content' : '')).join('\n')));
              }
              for (const id of r.retired) d.append(el('p', id + ': switched off', 'muted'));
              say(r.modules.length + ' module(s)');
            } catch (e) { say(e.message, true); }
          }
          $('modules').addEventListener('click', showModules);

          // Administration (admin role only; the API refuses everyone else). Read-only, except stopping jobs: policy changes go through proposals.
          const table = (heads, rows, nums) => {
            const t = el('table'); const h = el('tr'); for (const x of heads) h.append(el('th', x)); t.append(h);
            for (const r of rows) { const tr = el('tr'); r.forEach((v, i) => tr.append(el('td', v === null || v === undefined ? '' : String(v), nums && nums.includes(i) ? 'num' : ''))); t.append(tr); }
            return t;
          };
          let adminTab = 'jobs';
          async function showAdmin(tab) {
            current = null; if (tab) adminTab = tab;
            const d = $('detail'); d.replaceChildren();
            d.append(el('h2', 'Administration'));
            const tabs = el('div', null, 'tabs');
            for (const [k, label] of adminTabs) { const b = el('button', label, k === adminTab ? 'on' : ''); b.addEventListener('click', () => showAdmin(k)); tabs.append(b); }
            d.append(tabs);
            const body = el('div'); d.append(body);
            try { await (adminViews[adminTab])(body); say(''); } catch (e) { say(e.message, true); }
          }
          const adminTabs = [['jobs', 'Jobs'], ['agents', 'Agents'], ['people', 'People and keys'], ['policy', 'Policy versions'], ['health', 'Health'], ['usage', 'Usage'], ['telemetry', 'Telemetry'], ['audit', 'Audit'], ['proposals', 'Proposals']];
          const jobVerbs = { 'cancel': 'Cancel', 'run-now': 'Run now', 'redrive': 'Redrive', 'delete': 'Delete', 'stop-source': 'Stop the source' };
          const jobKinds = { work: 'Agent work', resume: 'Waiting to resume', queue: 'Queue' };
          const jobConfirm = {
            'cancel': 'Cancel this job? It will not run again. If it is running, its worker loses it.',
            'delete': 'Delete this dead job for good?',
            'stop-source': 'Stop what keeps creating this job? That is cancelled too, so it creates no more.',
          };
          function jobTable(rows, refresh) {
            const t = el('table'); const h = el('tr');
            for (const x of ['Kind', 'State', 'What', 'For', 'Resource', 'Source', 'Created', 'Attempts', '']) h.append(el('th', x)); t.append(h);
            for (const j of rows) {
              const tr = el('tr');
              const kind = jobKinds[j.kind] + (j.queue ? ' ' + j.queue : '');
              const runFor = j.runningSince ? ' · running ' + ago(j.runningSince).replace(' ago', '') + (j.maxRunSeconds ? ' of ' + Math.round(j.maxRunSeconds / 60) + ' min allowed' : '')
                + (j.progress ? ' · ' + j.progress : '') + (j.lastHeartbeat ? ' · last heartbeat ' + ago(j.lastHeartbeat) : '') : '';
              const state = j.state + runFor + (j.until && !j.runningSince ? ' · until ' + new Date(j.until).toLocaleString() : '') + (j.lastError ? ' · last error: ' + j.lastError : '');
              const stateCell = el('td', state);
              if (j.stuck) stateCell.append(el('span', 'stuck', 'tag bad'));
              tr.append(el('td', kind)); tr.append(stateCell);
              for (const v of [j.what, j.for, j.resource || '', j.source || 'not recorded', j.created ? ago(j.created) : '', j.attempts ?? '']) tr.append(el('td', String(v ?? '')));
              const cell = el('td');
              for (const a of j.actions) {
                const b = el('button', jobVerbs[a] || a);
                b.addEventListener('click', async () => {
                  if (jobConfirm[a] && !confirm(jobConfirm[a])) return;
                  try {
                    await api('/api/admin/jobs/act', { method: 'POST', body: JSON.stringify({ kind: j.kind, id: j.id, queue: j.queue || null, action: a }) });
                    say(jobVerbs[a] + ': done'); refresh();
                  } catch (e) { say(e.message, true); }
                });
                cell.append(b);
              }
              tr.append(cell); t.append(tr);
            }
            return t;
          }
          // After an outage: bring back the dead jobs that match, previewing how many first. Queue jobs come back through their
          // resource's limit and circuit, so they trickle instead of stampeding a resource that has only just recovered.
          function redriveForm(r, refresh) {
            const box = el('div');
            box.append(el('h3', 'Bring back dead jobs'));
            box.append(el('p', 'For example after an outage: every dead job for one resource since it went down. Preview first.', 'muted'));
            const where = el('select');
            for (const name of ['work', ...r.queues.map(q => q.name)]) { const o = el('option', name === 'work' ? 'Agent work' : 'Queue ' + name); o.value = name; where.append(o); }
            const input = (label, placeholder) => { const i = el('input'); i.placeholder = placeholder; const l = el('label', label + ' '); l.append(i); box.append(l, el('br')); return i; };
            const l0 = el('label', 'Where '); l0.append(where); box.append(l0, el('br'));
            const resource = input('Resource', 'db:reports01 or db:* (queue jobs)');
            const reason = input('Reason contains', 'refused, timed out ...');
            const source = input('Source', 'schedule:nightly');
            const since = input('Died since', '2026-10-22T09:00');
            const spread = input('Spread over (minutes)', '0: all at once (jobs with a resource still follow its limits)');
            const result = el('p', '', 'muted');
            const go = async (dryRun) => {
              const body = { queue: where.value, resource: resource.value, reasonContains: reason.value, source: source.value,
                deadSince: since.value ? new Date(since.value).toISOString() : '', spreadMinutes: Number(spread.value) || 0, dryRun };
              try {
                const x = await api('/api/admin/jobs/redrive-matching', { method: 'POST', body: JSON.stringify(body) });
                if (dryRun) result.textContent = x.count + ' dead job(s) match.';
                else { say(x.count + ' job(s) brought back'); refresh(); }
              } catch (e) { say(e.message, true); }
            };
            const preview = el('button', 'Preview'); preview.addEventListener('click', () => go(true));
            const redrive = el('button', 'Bring them back');
            redrive.addEventListener('click', () => { if (confirm('Bring back every dead job that matches? Each runs again from the start.')) go(false); });
            box.append(preview, redrive, result);
            return box;
          }
          const adminViews = {
            jobs: async (b) => {
              const r = await api('/api/admin/jobs');
              const refresh = () => showAdmin('jobs');
              b.append(el('p', 'Everything waiting or running: work owed by agents, continuations waiting to resume, and queue jobs. Cancel stops one job; if it keeps coming back, Stop the source turns off what creates it.', 'muted'));
              for (const q of r.queues) if (q.status !== 'healthy') b.append(el('p', 'Queue ' + q.name + ' needs attention: ' + q.reasons.join('; ') + '.', 'tag bad'));
              for (const n of r.notes) b.append(el('p', n, 'muted'));
              if (r.resources.length) {
                b.append(el('h3', 'Resources'));
                b.append(el('p', 'Jobs that load the same resource run no more than its limit at once. A circuit opens when the resource keeps failing: its jobs wait, using no attempts, and one is let through to test it.', 'muted'));
                b.append(table(['Queue', 'Resource', 'Running', 'Started (rate limit)', 'Waiting', 'Circuit'], r.resources.map(k => [k.queue, k.key,
                  k.running + (k.limit ? ' of ' + k.limit : ''), k.rate ? k.startedInWindow + ' (' + k.rate + ')' : '', k.waiting,
                  k.circuit === 'open' ? 'open until ' + new Date(k.openUntil).toLocaleTimeString() + ' (' + k.failures + ' failures)' : k.circuit === 'testing' ? 'testing one job' : 'closed'])));
              }
              b.append(el('h3', 'Waiting and running (' + r.pending.length + ')'));
              b.append(r.pending.length ? jobTable(r.pending, refresh) : el('p', 'Nothing waiting.', 'muted'));
              b.append(el('h3', 'Dead (' + r.dead.length + ')'));
              b.append(el('p', 'Jobs that ran out of attempts or time. Redrive runs one again from the start; Delete removes it.', 'muted'));
              if (r.deadGroups.length > 1 || (r.deadGroups.length === 1 && r.deadGroups[0].count > 1)) {
                const g = el('table'); const gh = el('tr'); for (const x of ['Where', 'Resource', 'Why', 'Jobs', 'Died', '']) gh.append(el('th', x)); g.append(gh);
                for (const d of r.deadGroups) {
                  const tr = el('tr');
                  for (const v of [d.queue === 'work' ? 'Agent work' : 'Queue ' + d.queue, d.resource || '', d.reason, d.count,
                    d.first === d.last ? ago(d.first) : ago(d.first) + ' to ' + ago(d.last)]) tr.append(el('td', String(v ?? '')));
                  const b2 = el('button', 'Bring back this group');
                  b2.addEventListener('click', async () => {
                    if (!confirm('Bring back ' + d.count + ' dead job(s)? Each runs again from the start.')) return;
                    try {
                      const x = await api('/api/admin/jobs/redrive-matching', { method: 'POST', body: JSON.stringify({ queue: d.queue, resource: d.resource || '', reasonContains: d.reason, dryRun: false }) });
                      say(x.count + ' job(s) brought back'); refresh();
                    } catch (e) { say(e.message, true); }
                  });
                  const c = el('td'); c.append(b2); tr.append(c); g.append(tr);
                }
                b.append(g);
              }
              b.append(r.dead.length ? jobTable(r.dead, refresh) : el('p', 'No dead jobs.', 'muted'));
              if (r.dead.length) b.append(redriveForm(r, refresh));
            },
            agents: async (b) => {
              const o = await api('/api/admin/overview');
              b.append(el('p', 'Policy ' + o.policy.hash.substring(0, 19) + '… loaded ' + (o.policy.loadedAt ? ago(o.policy.loadedAt) : 'now') + '. What each agent profile lets its agent call, and how the policy treats each call.', 'muted'));
              if (!o.agents.length) b.append(el('p', 'No agent profiles: every key sees every advertised tool.', 'muted'));
              for (const a of o.agents) {
                b.append(el('h3', a.name));
                b.append(el('p', 'Entries: ' + a.entries.join(', ') + (a.requestable.length ? ' · may ask for: ' + a.requestable.join(', ') : '') + (a.mayDelegateTo.length ? ' · may hand work to: ' + a.mayDelegateTo.join(', ') : ''), 'muted'));
                b.append(table(['Tool', 'Risk', 'Mode', 'After outside content'], a.tools.map(t => [t.name, t.risk, t.mode, t.modeAfterOutsideContent])));
                const p = el('button', 'Propose a change'); p.addEventListener('click', () => proposeForm(a)); b.append(p);
              }
              const n = el('button', 'Propose a new profile'); n.addEventListener('click', () => proposeForm(null)); b.append(el('p')); b.append(n);
            },
            people: async (b) => {
              const o = await api('/api/admin/overview');
              b.append(el('h3', 'People'));
              b.append(el('p', 'Who counts as the same person (for self-approval, groups and Teams).', 'muted'));
              b.append(table(['Person', 'Identities'], o.people.map(p => [p.person, p.identities.join(', ')])));
              b.append(el('h3', 'API keys'));
              b.append(el('p', 'As recorded at the last start. The key itself is never stored or shown.', 'muted'));
              b.append(table(['Name', 'Hash prefix', 'Agent profile'], o.keys.map(k => [k.name, k.hashPrefix + '…', k.profile || 'none'])));
            },
            policy: async (b) => {
              const r = await api('/api/admin/policy-versions');
              b.append(el('p', 'Every policy this database has run under. Pick one to see what changed from it to the running policy.', 'muted'));
              const out = el('div');
              const t = el('table'); const h = el('tr'); for (const x of ['Hash', 'First loaded', 'Last loaded', '']) h.append(el('th', x)); t.append(h);
              for (const v of r.versions.slice().reverse()) {
                const tr = el('tr');
                tr.append(el('td', v.hash.substring(0, 19) + '…'), el('td', new Date(v.firstLoaded).toLocaleString()), el('td', new Date(v.lastLoaded).toLocaleString()));
                const c = el('td');
                if (v.running) c.append(el('span', 'running', 'tag good'));
                else { const btn = el('button', 'Changes since'); btn.addEventListener('click', () => diff(v.hash, out)); c.append(btn); }
                tr.append(c); t.append(tr);
              }
              b.append(t); b.append(out);
            },
            health: async (b) => {
              const o = await api('/api/admin/overview');
              b.append(el('p', 'Status: ' + o.health.status + (o.health.lastSweep ? ' · last maintenance sweep ' + ago(o.health.lastSweep) : ' · no sweep yet') + (o.shadow ? ' · shadow mode' : '')));
              b.append(table(['Check', 'State'], Object.entries(o.health.checks)));
              b.append(el('h3', 'Startup warnings'));
              if (!o.warnings.length) b.append(el('p', 'None.', 'muted'));
              for (const w of o.warnings) b.append(el('p', '• ' + w));
            },
            usage: async (b) => {
              const r = await api('/api/admin/usage?days=7');
              if (r.note) { b.append(el('p', r.note, 'muted')); return; }
              b.append(el('p', 'Last 7 days, per identity, tool and day. Counts only: no arguments or results.', 'muted'));
              b.append(table(['Day', 'Identity', 'Profile', 'Tool', 'Calls', 'OK', 'Failed', 'Denied', 'Rejected', 'Records', 'Recipients'],
                r.rows.map(u => [u.day, u.subject, u.profile || '', u.tool, u.calls, u.succeeded, u.failed, u.denied, u.rejected, u.records, u.recipients]), [4, 5, 6, 7, 8, 9, 10]));
            },
          };
          // Telemetry recorded in this process since it started: no collector needed. Spans link to their action.
          const fmt = (n) => Math.abs(n) >= 100 ? Math.round(n).toLocaleString() : (Math.round(n * 10) / 10).toString();
          const bounds = [1, 5, 10, 50, 100, 500, 1000, 5000, 10000, 60000, 300000];
          const p95 = (s) => { if (!s.buckets) return null; const goal = s.count * 0.95; let n = 0; for (let i = 0; i < s.buckets.length; i++) { n += s.buckets[i]; if (n >= goal) return i < bounds.length ? '≤ ' + bounds[i] : '> ' + bounds[bounds.length - 1]; } return null; };
          const actionLink = (id) => { const a = el('a', id, 'link'); a.addEventListener('click', () => show(id)); return a; };
          adminViews.telemetry = async (b) => {
            const m = await api('/api/admin/telemetry/metrics');
            if (!m.enabled) { b.append(el('p', 'The telemetry recorder is off (RecordTelemetry: false in the HTTP config). Export with OpenTelemetry instead.', 'muted')); return; }
            b.append(el('p', 'Recorded in this process since ' + new Date(m.since).toLocaleString() + '. For history, export the OpenTail.Orca source and meter with OpenTelemetry.' + (m.droppedSeries ? ' ' + m.droppedSeries + ' measurements were not kept (too many tag combinations).' : ''), 'muted'));
            b.append(el('h3', 'Metrics'));
            if (!m.series.length) b.append(el('p', 'Nothing measured yet.', 'muted'));
            else b.append(table(['Instrument', 'Tags', 'Value', 'Count', 'Average', 'Max', 'p95'], m.series.map(s => [
              s.instrument + (s.unit ? ' (' + s.unit + ')' : ''), Object.entries(s.tags).map(([k, v]) => k + '=' + v).join(' '),
              s.kind === 'histogram' ? '' : fmt(s.value), s.count, s.kind === 'histogram' && s.count ? fmt(s.value / s.count) : '',
              s.kind === 'histogram' ? fmt(s.max) : '', s.kind === 'histogram' ? p95(s) : '']), [2, 3, 4, 5]));
            const t = await api('/api/admin/telemetry/traces?limit=300');
            b.append(el('h3', 'Recent traces'));
            if (!t.traces.length) { b.append(el('p', 'No spans yet.', 'muted')); return; }
            for (const tr of t.traces.slice(0, 60)) {
              const head = el('div', null, 'wfl');
              head.append(el('b', tr.root), el('span', new Date(tr.start).toLocaleTimeString() + (tr.error ? ' · error' : ''), tr.error ? 'err' : 'muted'), el('span', fmt(tr.durationMs) + ' ms', 'muted'));
              b.append(head);
              const t0 = new Date(tr.start).getTime(), total = Math.max(tr.durationMs, 0.001);
              for (const s of tr.spans) {
                const row = el('div', null, 'wfl');
                const label = el('span', (s.parentSpanId ? '  └ ' : '') + s.name);
                const bar = el('div', null, 'wf'); const fill = el('span', null, s.status === 'Error' ? 'bad' : '');
                fill.style.left = ((new Date(s.start).getTime() - t0) / total * 100).toFixed(2) + '%';
                fill.style.width = Math.max(s.durationMs / total * 100, 0.5).toFixed(2) + '%';
                fill.title = Object.entries(s.tags).map(([k, v]) => k + '=' + v).join('\n');
                bar.append(fill);
                const right = el('span', fmt(s.durationMs) + ' ms', 'muted');
                row.append(label, bar, right);
                b.append(row);
                const id = s.tags['opentail.action_id'];
                const info = Object.entries(s.tags).filter(([k]) => k !== 'opentail.action_id').map(([k, v]) => k.replace('opentail.', '') + '=' + v).join(' · ');
                const meta = el('p', info, 'muted'); meta.style.margin = '0 0 4px 16px'; meta.style.fontSize = '12px';
                if (id) { meta.append(' · '); meta.append(actionLink(id)); }
                b.append(meta);
              }
            }
          };
          adminViews.audit = async (b) => {
            const r = await api('/api/admin/audit?limit=200');
            b.append(el('p', 'The newest 200 entries of the audit trail, newest first. No argument values. The full trail: --export-audit.', 'muted'));
            const t = el('table'); const h = el('tr'); for (const x of ['When', 'Event', 'Who', 'Tool', 'Detail', 'Action']) h.append(el('th', x)); t.append(h);
            for (const e of r.entries) {
              const tr = el('tr');
              tr.append(el('td', new Date(e.at).toLocaleString()), el('td', e.event), el('td', e.actor || ''), el('td', e.tool || ''), el('td', e.detail || ''));
              const c = el('td'); if (e.actionId) c.append(actionLink(e.actionId)); tr.append(c);
              t.append(tr);
            }
            b.append(t);
          };
          // Policy proposals: the Host validates and replays; a second admin approves; the operator deploys the file.
          const list = (s) => s.split(',').map(x => x.trim()).filter(x => x);
          const changesView = (out, changes) => {
            if (!changes || !changes.length) { out.append(el('p', 'No differences.', 'muted')); return; }
            for (const c of changes) {
              out.append(el('p', c.kind + ': ' + c.section + (c.key ? ' / ' + c.key : ''), c.kind === 'removed' ? 'err' : ''));
              out.append(el('pre', (c.before !== null && c.before !== undefined ? '- ' + JSON.stringify(c.before, null, 2) + '\n' : '') + (c.after !== null && c.after !== undefined ? '+ ' + JSON.stringify(c.after, null, 2) : '')));
            }
          };
          adminViews.proposals = async (b) => {
            const rows = await api('/api/admin/proposals');
            b.append(el('p', 'A change to the policy is proposed here, checked against the loaded modules and replayed against stored actions, then approved by a second admin. The Host never changes its own policy: download the approved file, deploy it like any policy change, restart; the first start under it marks the proposal applied.', 'muted'));
            const n = el('button', 'New proposal', 'primary'); n.addEventListener('click', () => proposeForm(null)); b.append(n);
            if (!rows.length) { b.append(el('p', 'No proposals yet.', 'muted')); return; }
            const t = el('table'); const h = el('tr'); for (const x of ['Proposed', 'By', 'Status', 'Would change', 'Note']) h.append(el('th', x)); t.append(h);
            for (const p of rows) {
              const tr = el('tr'); tr.style.cursor = 'pointer';
              tr.append(el('td', ago(p.createdAt)), el('td', p.proposer), el('td', p.status + (p.stale ? ' (stale)' : '')), el('td', p.replay.changed + ' of ' + p.replay.examined), el('td', p.note || ''));
              tr.addEventListener('click', () => showProposal(p.id)); t.append(tr);
            }
            b.append(t);
          };
          async function proposeForm(agent) {
            current = null;
            const d = $('detail'); d.replaceChildren();
            d.append(el('h2', agent ? 'Change agent profile ' + agent.name : 'New proposal'));
            d.append(el('p', 'Entries are tool names, module.* or *, separated by commas. Administration tools need their exact name. Nothing changes until a second admin approves and the file is deployed.', 'muted'));
            const field = (label, value) => { const i = el('input'); i.value = value || ''; d.append(el('b', label)); d.append(i); d.append(el('p')); return i; };
            const name = field('Profile name', agent ? agent.name : '');
            if (agent) name.disabled = true;
            const tools = field('Tools', agent ? agent.entries.join(', ') : '');
            const req = field('May ask a person for (exact tool names)', agent ? agent.requestable.join(', ') : '');
            const del = field('May hand work to (profile names)', agent ? agent.mayDelegateTo.join(', ') : '');
            let remove = null;
            if (agent) { const l = el('label'); remove = el('input'); remove.type = 'checkbox'; remove.style.width = 'auto'; l.append(remove, ' Remove this profile'); d.append(l); d.append(el('p')); }
            const note = field('Why (shown to the approver and in the audit trail)', '');
            const adv = el('details'); adv.append(el('summary', 'Or edit the whole policy as JSON'));
            const whole = el('textarea'); whole.rows = 18; adv.append(whole); d.append(adv);
            let original = '';
            try { original = JSON.stringify(await api('/api/admin/policy/current'), null, 2); whole.value = original; } catch (e) { say(e.message, true); return; }
            const bar = el('div', null, 'actions'); const go = el('button', 'Check and propose', 'primary'); bar.append(go); d.append(bar);
            go.addEventListener('click', async () => {
              let policy;
              try { policy = JSON.parse(whole.value); } catch { say('The policy JSON is not valid.', true); return; }
              if (whole.value === original) {
                const n = name.value.trim();
                if (!n) { say('Give the profile a name.', true); return; }
                policy.Agents = policy.Agents || {};
                if (remove && remove.checked) delete policy.Agents[n];
                else policy.Agents[n] = { ...(policy.Agents[n] || {}), Tools: list(tools.value), Requestable: list(req.value), MayDelegateTo: list(del.value) };
              }
              try {
                const p = await api('/api/admin/proposals', { method: 'POST', body: JSON.stringify({ policy, note: note.value || null }) });
                say('Proposed: a second admin can approve it now.'); renderProposal(p);
              } catch (e) { say(e.message, true); }
            });
          }
          async function showProposal(id) { try { renderProposal(await api('/api/admin/proposals/' + encodeURIComponent(id))); } catch (e) { say(e.message, true); } }
          function renderProposal(p) {
            current = null;
            const d = $('detail'); d.replaceChildren();
            d.append(el('h2', 'Proposal ' + p.id));
            const tags = el('div'); tags.append(el('span', p.status, 'tag' + (p.status === 'Approved' || p.status === 'Applied' ? ' good' : p.status === 'Rejected' ? ' bad' : '')));
            if (p.stale) tags.append(el('span', 'stale: the policy changed since', 'tag warn'));
            d.append(tags);
            d.append(el('p', 'By ' + p.proposer + ' · ' + ago(p.createdAt) + (p.decidedBy ? ' · ' + p.status.toLowerCase() + ' by ' + p.decidedBy + (p.decidedAt ? ' ' + ago(p.decidedAt) : '') : '') + (p.reason ? ': ' + p.reason : ''), 'muted'));
            if (p.note) d.append(el('p', p.note));
            d.append(el('h3', 'What it would have changed'));
            d.append(el('p', p.replay.changed + ' of ' + p.replay.examined + ' stored actions would have been handled differently.' + (p.replay.changed ? '' : ' (Nothing stored would have gone differently.)')));
            for (const s of p.replay.summary) d.append(el('p', s, 'muted'));
            for (const x of p.replay.examples) { const r = el('p', x.tool + ': ' + x.was + ' → ' + x.wouldBe + ' · ', 'muted'); r.append(actionLink(x.actionId)); d.append(r); }
            if (p.warnings.length) { d.append(el('h3', 'Warnings')); for (const w of p.warnings) d.append(el('p', '• ' + w)); }
            d.append(el('h3', 'Changes'));
            changesView(d, p.changes);
            const bar = el('div', null, 'actions');
            const reason = el('input'); reason.placeholder = 'Reason (required to reject)';
            const act = (label, cls, decision) => { const btn = el('button', label, cls); btn.addEventListener('click', async () => {
              if (decision === 'reject' && !reason.value.trim()) { say('Give a reason to reject.', true); return; }
              try { renderProposal(await api('/api/admin/proposals/' + encodeURIComponent(p.id) + '/' + decision, { method: 'POST', body: JSON.stringify({ reason: reason.value || null }) })); say('Done'); }
              catch (e) { say(e.message, true); } }); return btn; };
            if (p.status === 'Proposed') bar.append(act('Approve', 'primary', 'approve'), act('Reject', 'danger', 'reject'), act('Withdraw', '', 'withdraw'));
            if (p.status === 'Approved') bar.append(act('Reject', 'danger', 'reject'));
            if (p.status === 'Approved' || p.status === 'Applied') {
              const dl = el('button', 'Download policy.json'); dl.addEventListener('click', async () => {
                try {
                  const r = await fetch('/api/admin/proposals/' + encodeURIComponent(p.id) + '/policy.json', { headers: { 'Authorization': 'Bearer ' + sessionStorage.getItem(tokenKey) } });
                  if (!r.ok) throw new Error('HTTP ' + r.status);
                  const a = el('a'); a.href = URL.createObjectURL(await r.blob()); a.download = 'policy-' + p.id + '.json'; a.click(); URL.revokeObjectURL(a.href);
                } catch (e) { say(e.message, true); }
              }); bar.append(dl);
            }
            if (p.status === 'Proposed' || p.status === 'Approved') { d.append(el('h3', 'Decision')); d.append(reason); }
            d.append(bar);
            if (p.status === 'Approved') d.append(el('p', 'Next: deploy the downloaded file as the policy (it replaces any Include files) and restart. Run --replay-policy against the real database first if you like.', 'muted'));
          }
          async function diff(from, out) {
            try {
              const r = await api('/api/admin/policy-versions/diff?from=' + encodeURIComponent(from));
              out.replaceChildren(el('h3', 'From ' + from.substring(0, 19) + '… to the running policy'));
              if (!r.changes.length) out.append(el('p', 'No differences.', 'muted'));
              for (const c of r.changes) {
                out.append(el('p', c.kind + ': ' + c.section + (c.key ? ' / ' + c.key : ''), c.kind === 'removed' ? 'err' : ''));
                out.append(el('pre', (c.before !== null && c.before !== undefined ? '- ' + JSON.stringify(c.before, null, 2) + '\n' : '') + (c.after !== null && c.after !== undefined ? '+ ' + JSON.stringify(c.after, null, 2) : '')));
              }
            } catch (e) { say(e.message, true); }
          }
          $('admin').addEventListener('click', () => showAdmin());
          async function whoAmI() { try { const me = await api('/api/me'); $('admin').hidden = !me.isAdmin; } catch { $('admin').hidden = true; } }
          $('connect').addEventListener('click', () => { sessionStorage.setItem(tokenKey, $('token').value); $('token').value = ''; loadList(); whoAmI(); const id = fromLink(); if (id) show(id); });
          $('filter').addEventListener('change', loadList);
          $('quick').addEventListener('change', loadList);
          document.addEventListener('keydown', (e) => {
            if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement || e.ctrlKey || e.metaKey || e.altKey) return;
            const i = shown.findIndex(a => a.id === current);
            if (e.key === 'j' && shown.length) show(shown[Math.min(i + 1, shown.length - 1)].id);
            else if (e.key === 'k' && shown.length) show(shown[Math.max(i - 1, 0)].id);
            else if (e.key === 'a' && lastDetail && lastDetail.status === 'Pending' && lastDetail.canDecide !== false) {
              if (confirm('Approve ' + lastDetail.tool + '?\n\n' + (lastDetail.previewSummary || ''))) decide(lastDetail.id, 'approve', '', '');
            }
            else if (e.key === 'x' && $('reason')) { e.preventDefault(); $('reason').focus(); say('Type the reason, then press Reject.'); }
            else return;
            e.preventDefault();
          });
          // A link from a notification (/#<action id>) only opens that action; deciding still needs sign-in and a click here.
          const fromLink = () => { const id = decodeURIComponent(location.hash.slice(1)); return /^[A-Za-z0-9_-]{1,80}$/.test(id) ? id : null; };
          window.addEventListener('hashchange', () => { const id = fromLink(); if (id && sessionStorage.getItem(tokenKey)) show(id); });

          // Sign-in with the company's identity provider: OpenID Connect authorization code with PKCE (no client secret in a browser).
          const b64url = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
          const pkceKey = 'opentail-sign-in';
          async function startSignIn(cfg) {
            try {
              const meta = await (await fetch(cfg.authority + '/.well-known/openid-configuration')).json();
              const verifier = b64url(crypto.getRandomValues(new Uint8Array(32)));
              const challenge = b64url(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
              const state = b64url(crypto.getRandomValues(new Uint8Array(16)));
              sessionStorage.setItem(pkceKey, JSON.stringify({ verifier, state, tokenEndpoint: meta.token_endpoint, hash: location.hash }));
              const u = new URL(meta.authorization_endpoint);
              u.search = new URLSearchParams({ client_id: cfg.clientId, response_type: 'code', redirect_uri: location.origin + '/', scope: cfg.scope,
                state, code_challenge: challenge, code_challenge_method: 'S256' }).toString();
              location.assign(u.toString());
            } catch (e) { say('Cannot reach the sign-in service: ' + e.message, true); }
          }
          async function finishSignIn(cfg) {
            const q = new URLSearchParams(location.search);
            if (!q.has('code') && !q.has('error')) return false;
            const saved = JSON.parse(sessionStorage.getItem(pkceKey) || 'null');
            sessionStorage.removeItem(pkceKey);
            history.replaceState(null, '', '/' + (saved && saved.hash ? saved.hash : ''));
            if (q.has('error')) { say('Sign-in was refused: ' + (q.get('error_description') || q.get('error')), true); return false; }
            if (!saved || q.get('state') !== saved.state) { say('That sign-in did not start here; please sign in again.', true); return false; }
            const r = await fetch(saved.tokenEndpoint, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
              body: new URLSearchParams({ grant_type: 'authorization_code', code: q.get('code'), redirect_uri: location.origin + '/',
                client_id: cfg.clientId, code_verifier: saved.verifier }) });
            const t = await r.json().catch(() => ({}));
            if (!t.access_token) { say('Sign-in failed: ' + (t.error_description || t.error || 'no token'), true); return false; }
            sessionStorage.setItem(tokenKey, t.access_token);
            return true;
          }
          (async () => {
            let cfg = null;
            try { cfg = await (await fetch('/sign-in.json')).json(); } catch { }
            if (cfg && cfg.clientId) {
              $('token').style.display = 'none';
              $('connect').textContent = 'Sign in';
              $('connect').replaceWith($('connect').cloneNode(true));
              $('connect').addEventListener('click', () => startSignIn(cfg));
              await finishSignIn(cfg);
            }
            if (sessionStorage.getItem(tokenKey)) { loadList(); whoAmI(); const id = fromLink(); if (id) show(id); }
            else say(fromLink() ? 'Sign in to open the action from your link.' : (cfg && cfg.clientId ? 'Sign in to begin.' : 'Enter the admin token to begin.'));
          })();
        })();
        </script>
        </body>
        </html>
        """;
}
