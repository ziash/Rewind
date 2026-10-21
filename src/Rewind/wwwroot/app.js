// Rewind page (docs/spec.md §5–§8). The server owns the replay cursor and the account: it never sends candles after the
// cursor, fills every order and pushes each change over a WebSocket. This page draws what it gets, asks for older candles
// at the left edge, sends the owner's orders, and lets SL / TP / order lines be dragged on the chart.
'use strict';

const LWC = window.LightweightCharts;
const PAGE = 500;
const $ = id => document.getElementById(id);

let info, view = { symbol: null, tf: null }, digits = 5;
let data = [], noMoreOlder = false, loadingOlder = false, loadSeq = 0;
let levelLines = [], picking = null; // null | 'level' | 'sl' | 'tp'
let account = null, session = null;
const tradeLines = new Map(); // key → price line of positions, orders, ask and the ticket's SL / TP
let drag = null;              // { key, line, price, apply(price) } while a line is being dragged

const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const fmt = (v, d = digits) => v == null ? '' : Number(v).toFixed(d);
const usd = v => (v >= 0 ? '' : '−') + Math.abs(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const specOf = symbol => info.specs.find(s => s.name === symbol);
const roundPrice = (p, d = digits) => Math.round(p * 10 ** d) / 10 ** d;

const chart = LWC.createChart($('chart'), {
  autoSize: true,
  layout: { background: { color: css('--bg') }, textColor: css('--text'), attributionLogo: true },
  grid: { vertLines: { color: css('--line') }, horzLines: { color: css('--line') } },
  rightPriceScale: { borderColor: css('--line') },
  timeScale: { borderColor: css('--line'), timeVisible: true, secondsVisible: false, rightOffset: 8, shiftVisibleRangeOnNewBar: true },
  crosshair: { mode: LWC.CrosshairMode.Normal },
});
const series = chart.addSeries(LWC.CandlestickSeries, {
  upColor: css('--up'), downColor: css('--down'), borderVisible: false,
  wickUpColor: css('--up'), wickDownColor: css('--down'),
});

// ---- server calls -------------------------------------------------------------------------------------------
async function call(method, url, body) {
  const r = await fetch(url, {
    method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) {
    const e = await r.json().catch(() => ({}));
    throw new Error(e.error || `${method} ${url}: HTTP ${r.status}`);
  }
  return r.status === 204 ? null : r.json();
}
const post = (url, body) => call('POST', url, body ?? {}).then(() => clearError()).catch(showError);

function showError(e) {
  $('status').textContent = e.message;
  $('status').className = 'error';
}
function clearError() {
  if ($('status').className === 'error') { $('status').textContent = ''; $('status').className = ''; }
}

// ---- candles ------------------------------------------------------------------------------------------------
async function reload() {
  const seq = ++loadSeq;
  const candles = await call('GET', `/api/candles?symbol=${view.symbol}&tf=${view.tf}&count=${PAGE}`);
  if (seq !== loadSeq) return; // a newer view replaced this one
  data = candles;
  noMoreOlder = candles.length < PAGE;
  series.setData(data);
  chart.timeScale().scrollToRealTime();
  await drawLevels();
  if (account) renderAccount(account);
}

async function loadOlder() {
  if (loadingOlder || noMoreOlder || data.length === 0) return;
  loadingOlder = true;
  const seq = loadSeq;
  try {
    const older = await call('GET', `/api/candles?symbol=${view.symbol}&tf=${view.tf}&before=${data[0].time}&count=${PAGE}`);
    if (seq !== loadSeq) return;
    noMoreOlder = older.length < PAGE;
    if (older.length) {
      const range = chart.timeScale().getVisibleLogicalRange();
      data = older.concat(data);
      series.setData(data);
      if (range) chart.timeScale().setVisibleLogicalRange({ from: range.from + older.length, to: range.to + older.length });
    }
  } finally {
    loadingOlder = false;
  }
}
chart.timeScale().subscribeVisibleLogicalRangeChange(r => { if (r && r.from < 30) loadOlder().catch(showError); });

function applyCandle(c) {
  if (!c || data.length === 0) return;
  const last = data[data.length - 1];
  if (c.time < last.time) return; // stale message
  if (c.time === last.time) data[data.length - 1] = c; else data.push(c);
  series.update({ ...c });
}

// ---- state from the server ----------------------------------------------------------------------------------
function showState(s) {
  $('utc').textContent = s.utc;
  $('ldn').textContent = s.london;
  $('ny').textContent = s.newYork;
  $('play').textContent = s.playing ? '❚❚ Pause' : '▶ Play';
  $('play').classList.toggle('on', s.playing);
  $('speed').value = s.speed;
  $('exact').classList.toggle('on', s.exact);
  if (document.activeElement !== $('gotoTime')) $('gotoTime').value = s.cursorUtc.slice(0, 16);
  if (s.symbol !== view.symbol || s.timeframe !== view.tf) setView(s.symbol, s.timeframe);
}

function setView(symbol, tf) {
  const symbolChanged = symbol !== view.symbol;
  view = { symbol, tf };
  $('symbol').value = symbol;
  for (const b of $('tfs').children) b.classList.toggle('on', b.textContent === tf);
  digits = specOf(symbol)?.digits ?? info.symbols.find(s => s.name === symbol).digits;
  series.applyOptions({ priceFormat: { type: 'price', precision: digits, minMove: 1 / 10 ** digits } });
  $('levelSymbol').textContent = symbol;
  $('tradeSymbol').textContent = symbol;
  const step = 1 / 10 ** digits;
  $('sl').step = $('tp').step = step;
  if (symbolChanged) { $('sl').value = ''; $('tp').value = ''; }
  const spec = specOf(symbol);
  if (spec) $('lots').min = $('lots').step = spec.stepVolume / spec.lotSize;
}

function connect() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`);
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    const changed = m.state.symbol !== view.symbol || m.state.timeframe !== view.tf;
    showState(m.state);
    if (m.session !== session) { session = m.session; loadSessions().catch(showError); }
    if (m.reload || changed) reload().catch(showError);
    else applyCandle(m.candle);
    if (m.account) { account = m.account; renderAccount(account); }
    $('tm').textContent = m.tm > 0 ? 'TM on' : 'TM off';
    $('tm').classList.toggle('on', m.tm > 0);
    showPacing(m.pacing);
  };
  ws.onclose = () => {
    showError(new Error('Connection to Rewind lost — retrying…'));
    setTimeout(connect, 2000);
  };
  ws.onopen = clearError;
}

// Exact mode (spec §6.2): while a price step waits for TM, say so in the footer (an error message stays).
function showPacing(symbol) {
  const st = $('status');
  if (st.className === 'error') return;
  st.textContent = symbol ? `Exact: waiting for TM (${symbol})` : '';
  st.className = symbol ? 'pacing' : '';
}

// ---- account, positions, orders, messages -------------------------------------------------------------------
function renderAccount(a) {
  $('balance').textContent = usd(a.balance);
  $('equity').textContent = usd(a.equity);
  $('openPnl').textContent = usd(a.openPnl);
  $('openPnl').className = a.openPnl > 0 ? 'pos' : a.openPnl < 0 ? 'neg' : '';
  $('commission').textContent = usd(a.commissionPaid);

  const q = a.quotes[view.symbol];
  $('bid').textContent = q ? fmt(q.bid) : '—';
  $('ask').textContent = q ? fmt(q.ask) : '—';
  $('buy').disabled = $('sell').disabled = !q || !q.live;
  $('buy').title = $('sell').title = q && !q.live ? 'Market closed' : '';

  renderPositions(a.positions);
  renderOrders(a.orders);
  renderEvents(a.events);
  drawTradeLines(a, q);
  $('closeAll').disabled = a.positions.length === 0 && a.orders.length === 0;
}

function renderPositions(positions) {
  const body = $('positions').querySelector('tbody');
  const rows = new Map([...body.rows].map(r => [r.dataset.id, r]));
  for (const p of positions) {
    const d = specOf(p.symbol)?.digits ?? 5;
    let row = rows.get(String(p.id));
    if (!row) {
      row = body.insertRow();
      row.dataset.id = p.id;
      row.innerHTML = `<td>${p.id}</td><td>${p.symbol}</td><td>${p.side}</td><td class="lotsCell"></td><td>${fmt(p.entry, d)}</td>` +
        `<td><input class="sl" type="number" step="${1 / 10 ** d}" placeholder="none"></td>` +
        `<td><input class="tp" type="number" step="${1 / 10 ** d}" placeholder="none"></td>` +
        `<td class="price"></td><td class="pips"></td><td class="pnl"></td><td class="label"></td>` +
        `<td class="lots"><input class="closeLots" type="number" step="0.01" min="0.01"></td><td><button class="close">Close</button></td>`;
      const protect = () => {
        const sl = row.querySelector('.sl').value, tp = row.querySelector('.tp').value;
        post(`/api/positions/${p.id}/protection`, { stopLoss: sl === '' ? null : +sl, takeProfit: tp === '' ? null : +tp });
      };
      row.querySelector('.sl').onchange = protect;
      row.querySelector('.tp').onchange = protect;
      row.querySelector('.close').onclick = () => {
        const lots = row.querySelector('.closeLots').value;
        post(`/api/positions/${p.id}/close`, { lots: lots === '' ? null : +lots });
      };
    }
    rows.delete(String(p.id));
    row.querySelector('.lotsCell').textContent = p.lots.toFixed(2);
    setInput(row.querySelector('.sl'), fmt(p.stopLoss, d));
    setInput(row.querySelector('.tp'), fmt(p.takeProfit, d));
    setInput(row.querySelector('.closeLots'), p.lots.toFixed(2));
    row.querySelector('.price').textContent = fmt(p.price, d);
    row.querySelector('.pips').textContent = p.pips.toFixed(1);
    const pnl = row.querySelector('.pnl');
    pnl.textContent = usd(p.pnlUsd);
    pnl.className = 'pnl ' + (p.pnlUsd > 0 ? 'pos' : p.pnlUsd < 0 ? 'neg' : '');
    row.querySelector('.label').textContent = p.label || 'manual';
  }
  for (const r of rows.values()) r.remove();
  $('noPositions').hidden = positions.length > 0;
}

/** Updates an input from the server unless the owner is typing in it, or the server value has not changed. */
function setInput(input, value) {
  if (document.activeElement === input || input.dataset.server === value) return;
  input.dataset.server = value;
  input.value = value;
}

function renderOrders(orders) {
  $('orders').hidden = orders.length === 0;
  $('orders').querySelector('tbody').replaceChildren(...orders.map(o => {
    const d = specOf(o.symbol)?.digits ?? 5;
    const tr = document.createElement('tr');
    tr.innerHTML = `<td>${o.id}</td><td>${o.symbol}</td><td>${o.side}-stop</td><td>${o.lots.toFixed(2)}</td><td>${fmt(o.price, d)}</td><td></td><td><button>Cancel</button></td>`;
    tr.cells[5].textContent = o.label || 'manual';
    tr.querySelector('button').onclick = () => call('DELETE', `/api/orders/${o.id}`).then(clearError).catch(showError);
    return tr;
  }));
}

function renderEvents(events) {
  $('events').replaceChildren(...events.slice(0, 40).map(e => {
    const li = document.createElement('li');
    const t = document.createElement('time');
    t.textContent = e.timeUtc.slice(5, 16).replace('T', ' ');
    li.append(t, `${e.symbol} #${e.positionId ?? e.orderId} ${e.detail}${e.label ? ' · ' + e.label : ''}`);
    return li;
  }));
}

// ---- lines on the chart: positions, SL / TP, orders, ask, the ticket's SL / TP -------------------------------
function setLine(key, options) {
  if (drag?.key === key) return; // the owner is moving it
  const line = tradeLines.get(key);
  if (line) line.applyOptions(options);
  else tradeLines.set(key, series.createPriceLine({ lineWidth: 1, axisLabelVisible: true, ...options }));
}

function drawTradeLines(a, q) {
  const keep = new Set();
  const want = (key, options) => { keep.add(key); setLine(key, options); };
  if (q) want('ask', { price: q.ask, color: css('--accent'), lineStyle: LWC.LineStyle.Dotted, title: 'ask', axisLabelVisible: false });
  for (const p of a.positions.filter(p => p.symbol === view.symbol)) {
    const tag = `#${p.id} ${p.side} ${p.lots.toFixed(2)}${p.label ? ' ' + p.label.split('-').pop() : ''}`;
    want(`e${p.id}`, { price: p.entry, color: css('--entry'), lineStyle: LWC.LineStyle.Solid, title: tag });
    if (p.stopLoss != null) want(`s${p.id}`, { price: p.stopLoss, color: css('--down'), lineStyle: LWC.LineStyle.Dashed, title: `SL #${p.id}` });
    if (p.takeProfit != null) want(`t${p.id}`, { price: p.takeProfit, color: css('--up'), lineStyle: LWC.LineStyle.Dashed, title: `TP #${p.id}` });
  }
  for (const o of a.orders.filter(o => o.symbol === view.symbol))
    want(`o${o.id}`, { price: o.price, color: css('--order'), lineStyle: LWC.LineStyle.Dashed, title: `#${o.id} ${o.side}-stop ${o.lots.toFixed(2)}` });
  if ($('sl').value !== '') want('ticketSl', { price: +$('sl').value, color: css('--down'), lineStyle: LWC.LineStyle.SparseDotted, title: 'new SL' });
  if ($('tp').value !== '') want('ticketTp', { price: +$('tp').value, color: css('--up'), lineStyle: LWC.LineStyle.SparseDotted, title: 'new TP' });
  for (const [key, line] of tradeLines) {
    if (!keep.has(key) && drag?.key !== key) { series.removePriceLine(line); tradeLines.delete(key); }
  }
}

/** Lines that can be dragged: a position's SL / TP, a pending order. Returns what dropping it at a price does. */
function draggable(key) {
  const id = +key.slice(1);
  if (key[0] === 's' || key[0] === 't') {
    const p = account.positions.find(x => x.id === id);
    if (!p) return null;
    return price => post(`/api/positions/${id}/protection`,
      key[0] === 's' ? { stopLoss: price, takeProfit: p.takeProfit } : { stopLoss: p.stopLoss, takeProfit: price });
  }
  if (key[0] === 'o') return price => post(`/api/orders/${id}/amend`, { price });
  if (key === 'ticketSl') return price => { $('sl').value = fmt(price); drawTradeLines(account, account.quotes[view.symbol]); };
  if (key === 'ticketTp') return price => { $('tp').value = fmt(price); drawTradeLines(account, account.quotes[view.symbol]); };
  return null;
}

function lineNear(y) {
  for (const [key, line] of tradeLines) {
    if (!/^(s|t|o)\d+$|^ticket/.test(key)) continue;
    const ly = series.priceToCoordinate(line.options().price);
    if (ly != null && Math.abs(ly - y) <= 5 && draggable(key)) return { key, line };
  }
  return null;
}

const chartEl = $('chart');
const localY = e => e.clientY - chartEl.getBoundingClientRect().top;
chartEl.addEventListener('pointerdown', e => {
  if (picking || !account || e.button !== 0) return;
  const hit = lineNear(localY(e));
  if (!hit) return;
  e.preventDefault();
  e.stopPropagation(); // keep the chart from scrolling while a line is dragged
  drag = { ...hit, price: hit.line.options().price, apply: draggable(hit.key) };
  chartEl.setPointerCapture(e.pointerId);
  chartEl.classList.add('dragging');
}, true);
chartEl.addEventListener('pointermove', e => {
  if (!drag) {
    chartEl.classList.toggle('near-line', !picking && !!account && !!lineNear(localY(e)));
    return;
  }
  e.stopPropagation();
  const price = series.coordinateToPrice(localY(e));
  if (price == null) return;
  drag.price = roundPrice(price);
  drag.line.applyOptions({ price: drag.price });
}, true);
chartEl.addEventListener('pointerup', e => {
  if (!drag) return;
  e.stopPropagation();
  const { apply, price } = drag;
  drag = null;
  chartEl.classList.remove('dragging');
  apply(price);
  if (account) renderAccount(account); // back to the server's prices if it refuses
}, true);

// ---- levels -------------------------------------------------------------------------------------------------
async function drawLevels() {
  const symbol = view.symbol;
  const levels = await call('GET', `/api/levels?symbol=${symbol}`);
  if (symbol !== view.symbol) return;
  for (const l of levelLines) series.removePriceLine(l);
  levelLines = levels.map(l => series.createPriceLine({
    price: l.price, color: css('--level'), lineWidth: 1, lineStyle: LWC.LineStyle.Dashed, axisLabelVisible: true, title: l.note,
  }));
  $('levels').replaceChildren(...levels.map(l => {
    const li = document.createElement('li');
    const price = document.createElement('span');
    price.className = 'price';
    price.textContent = l.price.toFixed(digits);
    const note = document.createElement('span');
    note.className = 'note';
    note.textContent = l.note;
    const del = document.createElement('button');
    del.textContent = '×';
    del.title = 'Remove level';
    del.onclick = () => call('DELETE', `/api/levels/${l.id}`).then(drawLevels).catch(showError);
    li.append(price, note, del);
    return li;
  }));
}

function setPicking(what) {
  picking = what;
  chartEl.classList.toggle('picking', !!what);
  $('addLevel').classList.toggle('on', what === 'level');
  $('pickSl').classList.toggle('on', what === 'sl');
  $('pickTp').classList.toggle('on', what === 'tp');
}

chart.subscribeClick(p => {
  const what = picking ?? (p.sourceEvent?.altKey ? 'level' : null);
  if (!p.point || !what) return;
  const price = series.coordinateToPrice(p.point.y);
  setPicking(null);
  if (price == null) return;
  const rounded = roundPrice(price);
  if (what === 'sl' || what === 'tp') {
    $(what).value = fmt(rounded);
    if (account) drawTradeLines(account, account.quotes[view.symbol]);
    return;
  }
  call('POST', '/api/levels', { symbol: view.symbol, price: rounded, note: $('levelNote').value })
    .then(() => { $('levelNote').value = ''; return drawLevels(); })
    .catch(showError);
});

// ---- sessions -----------------------------------------------------------------------------------------------
async function loadSessions() {
  const s = await call('GET', '/api/sessions');
  $('session').replaceChildren(...s.all.map(n => new Option(n, n)));
  $('session').value = s.current;
}

$('session').onchange = e => {
  call('POST', '/api/sessions/open', { name: e.target.value }).then(clearError)
    .catch(err => { $('session').value = session; showError(err); });
};
$('newSession').onclick = () => {
  $('sessionName').value = '';
  $('sessionStart').value = $('gotoTime').value;
  $('sessionBalance').value = info.startBalance;
  $('sessionError').textContent = '';
  $('sessionDialog').showModal();
};
// The form's submit event, not the dialog's close event (which some browsers fire late or not at all): the dialog
// stays open with the reason when the server refuses (e.g. positions still open).
$('sessionForm').addEventListener('submit', e => {
  if (e.submitter?.value !== 'create') return; // Cancel just closes
  e.preventDefault();
  $('sessionError').textContent = '';
  call('POST', '/api/sessions', { name: $('sessionName').value, start: $('sessionStart').value, balance: +$('sessionBalance').value })
    .then(() => { $('sessionDialog').close(); clearError(); })
    .catch(err => { $('sessionError').textContent = err.message; });
});

// ---- controls -----------------------------------------------------------------------------------------------
function playing() { return $('play').classList.contains('on'); }

function order(side) {
  const sl = $('sl').value, tp = $('tp').value;
  call('POST', '/api/orders/market', {
    symbol: view.symbol, side, lots: +$('lots').value,
    stopLoss: sl === '' ? null : +sl, takeProfit: tp === '' ? null : +tp,
  }).then(() => { clearError(); $('sl').value = ''; $('tp').value = ''; if (account) renderAccount(account); }).catch(showError);
}

$('buy').onclick = () => order('buy');
$('sell').onclick = () => order('sell');
$('sl').oninput = $('tp').oninput = () => account && drawTradeLines(account, account.quotes[view.symbol]);
$('pickSl').onclick = () => setPicking(picking === 'sl' ? null : 'sl');
$('pickTp').onclick = () => setPicking(picking === 'tp' ? null : 'tp');
$('closeAll').onclick = () => { if (confirm('Close every position and cancel every order?')) post('/api/positions/close-all'); };
$('symbol').onchange = e => post('/api/view', { symbol: e.target.value, timeframe: view.tf });
$('step').onclick = () => post('/api/step');
$('play').onclick = () => playing() ? post('/api/pause') : post('/api/play', { speed: +$('speed').value });
$('speed').onchange = () => { if (playing()) post('/api/play', { speed: +$('speed').value }); };
$('exact').onclick = () => post('/api/mode', { exact: !$('exact').classList.contains('on') });
$('goto').onsubmit = e => { e.preventDefault(); post('/api/goto', { time: $('gotoTime').value }); $('gotoTime').blur(); };
$('addLevel').onclick = () => setPicking(picking === 'level' ? null : 'level');
for (const b of document.querySelectorAll('[data-skip]')) b.onclick = () => post('/api/skip', { to: b.dataset.skip });

document.addEventListener('keydown', e => {
  if (e.target.closest('input, select, textarea, dialog')) return;
  if (e.key === 'ArrowRight') { e.preventDefault(); post('/api/step'); }
  else if (e.key === ' ') { e.preventDefault(); $('play').click(); }
  else if (e.key === 'Escape') setPicking(null);
});

// ---- start --------------------------------------------------------------------------------------------------
(async () => {
  try {
    info = await call('GET', '/api/info');
    $('symbol').replaceChildren(...info.symbols.map(s => new Option(s.name, s.name)));
    $('tfs').replaceChildren(...info.timeframes.map(tf => {
      const b = document.createElement('button');
      b.textContent = tf;
      b.onclick = () => post('/api/view', { symbol: view.symbol, timeframe: tf });
      return b;
    }));
    $('speed').replaceChildren(...info.speeds.map(s => new Option(s, s)));
    $('gotoTime').min = $('sessionStart').min = info.minUtc.slice(0, 16);
    $('gotoTime').max = $('sessionStart').max = info.maxUtc.slice(0, 16);
    connect(); // its first message sets the view, which loads the candles
  } catch (e) {
    showError(e);
  }
})();
