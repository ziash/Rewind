// Rewind chart page (docs/spec.md §5, §6). The server owns the replay cursor and never sends candles after it;
// this page draws what it gets, asks for older candles when scrolled to the left edge, and follows the server's
// updates over a WebSocket.
'use strict';

const LWC = window.LightweightCharts;
const PAGE = 500;
const $ = id => document.getElementById(id);

let info, view = { symbol: null, tf: null }, digits = 5;
let data = [], noMoreOlder = false, loadingOlder = false, loadSeq = 0;
let levelLines = [], picking = false;

const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

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
const post = (url, body) => call('POST', url, body ?? {}).catch(showError);

function showError(e) {
  $('status').textContent = e.message;
  $('status').className = 'error';
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
  series.update(c);
}

// ---- state from the server ----------------------------------------------------------------------------------
function showState(s) {
  $('utc').textContent = s.utc;
  $('ldn').textContent = s.london;
  $('ny').textContent = s.newYork;
  $('play').textContent = s.playing ? '❚❚ Pause' : '▶ Play';
  $('play').classList.toggle('on', s.playing);
  $('speed').value = s.speed;
  if (document.activeElement !== $('gotoTime')) $('gotoTime').value = s.cursorUtc.slice(0, 16);
  if (s.symbol !== view.symbol || s.timeframe !== view.tf) setView(s.symbol, s.timeframe);
}

function setView(symbol, tf) {
  view = { symbol, tf };
  $('symbol').value = symbol;
  for (const b of $('tfs').children) b.classList.toggle('on', b.textContent === tf);
  digits = info.symbols.find(s => s.name === symbol).digits;
  series.applyOptions({ priceFormat: { type: 'price', precision: digits, minMove: 1 / 10 ** digits } });
  $('levelSymbol').textContent = symbol;
}

function connect() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`);
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    const changed = m.state.symbol !== view.symbol || m.state.timeframe !== view.tf;
    showState(m.state);
    if (m.reload || changed) reload().catch(showError);
    else applyCandle(m.candle);
    if ($('status').className !== 'error') $('status').textContent = '';
  };
  ws.onclose = () => {
    $('status').textContent = 'Connection to Rewind lost — retrying…';
    $('status').className = 'error';
    setTimeout(() => { $('status').className = ''; connect(); }, 2000);
  };
}

// ---- levels -------------------------------------------------------------------------------------------------
async function drawLevels() {
  const symbol = view.symbol;
  const levels = await call('GET', `/api/levels?symbol=${symbol}`);
  if (symbol !== view.symbol) return;
  for (const l of levelLines) series.removePriceLine(l);
  levelLines = levels.map(l => series.createPriceLine({
    price: l.price, color: css('--level'), lineWidth: 1, lineStyle: LWC.LineStyle.Dashed, axisLabelVisible: true, title: l.note,
  }));
  const ul = $('levels');
  ul.replaceChildren(...levels.map(l => {
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

function setPicking(on) {
  picking = on;
  $('chart').classList.toggle('picking', on);
  $('addLevel').classList.toggle('on', on);
}

chart.subscribeClick(p => {
  if (!p.point || !(picking || p.sourceEvent?.altKey)) return;
  const price = series.coordinateToPrice(p.point.y);
  setPicking(false);
  if (price == null) return;
  const rounded = Math.round(price * 10 ** digits) / 10 ** digits;
  call('POST', '/api/levels', { symbol: view.symbol, price: rounded, note: $('levelNote').value })
    .then(() => { $('levelNote').value = ''; return drawLevels(); })
    .catch(showError);
});

// ---- controls -----------------------------------------------------------------------------------------------
function playing() { return $('play').classList.contains('on'); }

$('symbol').onchange = e => post('/api/view', { symbol: e.target.value, timeframe: view.tf });
$('step').onclick = () => post('/api/step');
$('play').onclick = () => playing() ? post('/api/pause') : post('/api/play', { speed: +$('speed').value });
$('speed').onchange = () => { if (playing()) post('/api/play', { speed: +$('speed').value }); };
$('goto').onsubmit = e => { e.preventDefault(); post('/api/goto', { time: $('gotoTime').value }); $('gotoTime').blur(); };
$('addLevel').onclick = () => setPicking(!picking);
for (const b of document.querySelectorAll('[data-skip]')) b.onclick = () => post('/api/skip', { to: b.dataset.skip });

document.addEventListener('keydown', e => {
  if (e.target.closest('input, select, textarea')) return;
  if (e.key === 'ArrowRight') { e.preventDefault(); post('/api/step'); }
  else if (e.key === ' ') { e.preventDefault(); $('play').click(); }
  else if (e.key === 'Escape') setPicking(false);
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
    $('gotoTime').min = info.minUtc.slice(0, 16);
    $('gotoTime').max = info.maxUtc.slice(0, 16);
    connect(); // its first message sets the view, which loads the candles
  } catch (e) {
    showError(e);
  }
})();
