import { ESPLoader, Transport } from '../vendor/esptool-js/bundle.js';
import { md5 } from './md5.js';
import { t } from './messages.js';
import { initStats } from './stats.js';

const USB_CHIPS = { 0x1a86: 'CH340', 0x10c4: 'CP210x', 0x0403: 'FTDI', 0x303a: 'ESP32 USB' };
const BAUD_APP = 921600;
const BAUD_FLASH = 921600;
const BAUD_ROM = 115200;
const PANEL_ENV = { ILI9341: 'cyd', ILI9341_INV: 'cyd', ST7789: 'cyd2usb' };
const PANEL_RE = /CYD_PANEL=(\w+) id=([0-9A-Fa-f]+) build=(\w+)/;
const DRIVER_URL = 'https://www.wch-ic.com/downloads/CH341SER_EXE.html';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const hex4 = (n) => (n == null ? '????' : n.toString(16).padStart(4, '0'));

class FlashError extends Error {
  constructor(kind, message) { super(message || kind); this.kind = kind; }
}

export function initFlasher({ config, getLang, onLangChange }) {
  const $ = (id) => document.getElementById(id);
  const el = {
    modes: $('modes'), start: $('flash-start'), busy: $('flash-busy'),
    dot: $('st-dot'), text: $('st-text'), chip: $('st-chip'), steps: $('steps'),
    barWrap: $('bar-wrap'), barLabel: $('bar-label'), barPct: $('bar-pct'), bar: $('bar'), barFill: $('bar-fill'),
    result: $('result'), resultTitle: $('result-title'), resultText: $('result-text'), resultActions: $('result-actions'),
    log: $('log'), logCopy: $('log-copy'),
  };
  if (!el.start) return;

  const stats = initStats(config);
  const repo = `https://github.com/${config.repo}`;
  const supported = 'serial' in navigator && window.isSecureContext;

  const ui = {
    status: 'idle',
    chip: { key: supported ? 'chip.ready' : 'chip.none' },
    steps: [],
    bar: null,
    result: null,
    log: [{ key: 'log.idle', cls: 'idle' }],
  };
  let manifest = null;
  let builds = {};
  let raw = [];
  let busy = false;
  let showAllPorts = false;

  const tr = (key, vars) => t(getLang(), key, vars);

  function render() {
    const dotColor = { idle: '#7d877f', busy: '#ffb02e', done: '#8bd96a', error: '#ff7a72' }[ui.status];
    el.dot.style.background = dotColor;
    el.text.textContent = tr(`status.${ui.status}`);
    el.chip.textContent = ui.chip.raw || tr(ui.chip.key, ui.chip.vars);

    el.steps.replaceChildren(...ui.steps.map((s) => {
      const li = document.createElement('li');
      li.className = s.state;
      const mark = document.createElement('span');
      mark.className = 'mark';
      mark.setAttribute('aria-hidden', 'true');
      const text = document.createElement('span');
      text.className = 'step-text';
      const label = document.createElement('span');
      label.className = 'step-label';
      label.textContent = tr(`step.${s.id}`);
      const sub = document.createElement('span');
      sub.className = 'step-sub' + (s.sub && s.sub.warn ? ' warn' : '');
      sub.textContent = s.sub ? tr(s.sub.key, s.sub.vars) : tr(`step.${s.id}.sub`, s.vars);
      text.append(label, sub);
      li.append(mark, text);
      return li;
    }));

    el.barWrap.hidden = !ui.bar;
    if (ui.bar) {
      el.barLabel.textContent = tr(ui.bar.key);
      el.barPct.textContent = `${ui.bar.pct}%`;
      el.barFill.style.width = `${ui.bar.pct}%`;
      el.bar.setAttribute('aria-valuenow', String(ui.bar.pct));
      el.bar.setAttribute('aria-label', tr(ui.bar.key));
    }

    el.result.hidden = !ui.result;
    if (ui.result) {
      const r = ui.result;
      el.result.className = `result ${r.kind}`;
      el.resultTitle.textContent = tr(r.title, r.vars);
      el.resultText.innerHTML = tr(r.text, r.vars) + (r.extra ? `<br>${tr(r.extra, r.vars)}` : '');
      el.resultActions.replaceChildren(...(r.actions || []).map((a) => {
        const node = document.createElement(a.href ? 'a' : 'button');
        node.className = 'btn btn--secondary';
        node.textContent = a.done ? tr(a.done) : tr(a.label);
        if (a.href) {
          node.href = a.href;
          if (/^https?:/.test(a.href)) { node.target = '_blank'; node.rel = 'noopener'; }
        } else {
          node.type = 'button';
          node.addEventListener('click', a.onClick);
        }
        return node;
      }));
    }

    el.log.replaceChildren(...ui.log.slice(-8).map((l) => {
      const div = document.createElement('div');
      if (l.cls) div.className = l.cls;
      div.textContent = (l.cls === 'idle' ? '' : '> ') + tr(l.key, l.vars);
      return div;
    }));
    el.logCopy.hidden = raw.length === 0 || busy;

    el.start.hidden = busy;
    el.busy.hidden = !busy;
    el.start.disabled = !supported || (!manifest && ui.result && ui.result.kind === 'warn');
    for (const input of el.modes.querySelectorAll('input')) input.disabled = busy || !supported;
  }

  const step = (id) => ui.steps.find((s) => s.id === id);
  function setActive(id) {
    for (const s of ui.steps) if (s.state === 'active') s.state = 'done';
    const s = step(id);
    if (s) s.state = 'active';
    render();
  }
  function setDone(id, sub) {
    const s = step(id);
    if (s) { s.state = 'done'; if (sub) s.sub = sub; }
    render();
  }
  function setSub(id, sub) { const s = step(id); if (s) s.sub = sub; render(); }
  function insertBefore(beforeId, id) {
    if (step(id)) return;
    const i = ui.steps.findIndex((s) => s.id === beforeId);
    ui.steps.splice(i < 0 ? ui.steps.length : i, 0, { id, state: 'pending' });
    render();
  }
  function log(key, vars, cls = 'now') {
    for (const l of ui.log) if (l.cls === 'now') l.cls = '';
    ui.log = ui.log.filter((l) => l.cls !== 'idle');
    ui.log.push({ key, vars, cls });
    raw.push(`[web] ${t('en', key, vars)}`);
    render();
  }
  function rawLine(s) {
    const clean = String(s).replace(/[^\x20-\x7EÀ-ỹ]/g, '').trim();
    if (clean) raw.push(clean);
    if (raw.length > 4000) raw = raw.slice(-3000);
  }

  async function loadManifest() {
    try {
      const res = await fetch('firmware/manifest.json', { cache: 'no-cache' });
      if (!res.ok) throw new Error(res.status);
      manifest = await res.json();
      for (const node of document.querySelectorAll('[data-fw-version]')) node.textContent = `v${manifest.version}`;
    } catch {
      manifest = null;
    }
  }

  async function refreshManifest() {
    const before = manifest;
    await loadManifest();
    if (!manifest) manifest = before;
  }

  async function fetchBuild(env) {
    const b = manifest && manifest.builds && manifest.builds[env];
    if (!b) throw new FlashError('download', `no build ${env}`);
    const key = b.parts.map((p) => p.md5 || manifest.version).join(',');
    if (builds[env] && builds[env].key === key) return builds[env].files;
    try {
      const files = await Promise.all(b.parts.map(async (p) => {
        const res = await fetch(`firmware/${p.path}?v=${p.md5 || manifest.version}`);
        if (!res.ok) throw new Error(`HTTP ${res.status} ${p.path}`);
        const data = new Uint8Array(await res.arrayBuffer());
        if (p.md5 && md5(data) !== p.md5) throw new Error(`MD5 ${p.path}`);
        return { data, address: p.offset };
      }));
      builds[env] = { key, files };
      return files;
    } catch (e) {
      throw new FlashError('download', e.message);
    }
  }

  function portName(port) {
    const info = port.getInfo ? port.getInfo() : {};
    const name = USB_CHIPS[info.usbVendorId];
    const id = info.usbVendorId != null ? `${hex4(info.usbVendorId)}:${hex4(info.usbProductId)}` : 'USB';
    return name ? `${name} (${id})` : id;
  }

  async function openPort(port, baudRate) {
    if (port.readable || port.writable) { try { await port.close(); } catch {} }
    try {
      await port.open({ baudRate, bufferSize: 65536 });
    } catch (e) {
      throw new FlashError('busy', e.message);
    }
  }

  async function closePort(port) {
    try { await port.close(); } catch {}
  }

  async function readPanel(port, timeoutMs = 7000) {
    await port.setSignals({ dataTerminalReady: false, requestToSend: true });
    await sleep(150);
    await port.setSignals({ dataTerminalReady: false, requestToSend: false });
    const reader = port.readable.getReader();
    const dec = new TextDecoder();
    let text = '';
    let found = null;
    const deadline = Date.now() + timeoutMs;
    try {
      while (!found && Date.now() < deadline) {
        const r = await Promise.race([reader.read(), sleep(deadline - Date.now()).then(() => ({ timeout: true }))]);
        if (r.timeout || r.done) break;
        text += dec.decode(r.value, { stream: true });
        const m = text.match(PANEL_RE);
        if (m) found = { panel: m[1], id: m[2], build: m[3] };
        if (text.length > 32000) text = text.slice(-4000);
      }
    } finally {
      try { await reader.cancel(); } catch {}
      reader.releaseLock();
    }
    for (const line of text.split(/\r?\n/)) rawLine(line);
    return found;
  }

  async function detectPanel(port) {
    await openPort(port, BAUD_APP);
    log('log.reset');
    try {
      return await readPanel(port);
    } finally {
      await closePort(port);
    }
  }

  async function connectLoader(port) {
    const transport = new Transport(port, false);
    const terminal = { clean() {}, writeLine: (s) => rawLine(s), write: (s) => rawLine(s) };
    const loader = new ESPLoader({ transport, baudrate: BAUD_FLASH, romBaudrate: BAUD_ROM, terminal });
    log('log.sync');
    let chip;
    try {
      chip = await loader.main();
    } catch (e) {
      try { await transport.disconnect(); } catch {}
      const msg = String(e && e.message);
      if (/open/i.test(msg) && /port/i.test(msg)) throw new FlashError('busy', msg);
      throw new FlashError('sync', msg);
    }
    let flash = '?';
    try { flash = (await loader.detectFlashSize()) || '?'; } catch {}
    log('log.chip', { chip, flash });
    return { transport, loader, chip, flash };
  }

  async function closeLoader(ld) {
    try { await ld.loader.after('hard_reset'); } catch {}
    try { await ld.transport.disconnect(); } catch {}
  }

  async function writeBuild(ld, env, stepId) {
    const files = await fetchBuild(env);
    const sizes = files.map((f) => f.data.length);
    const total = sizes.reduce((a, b) => a + b, 0);
    log('log.write', { env });
    ui.bar = { key: stepId === 'rewrite' ? 'bar.rewrite' : 'bar.write', pct: 0 };
    render();
    let last = -1;
    try {
      await ld.loader.writeFlash({
        fileArray: files.map((f) => ({ data: f.data, address: f.address })),
        flashMode: 'keep', flashFreq: 'keep', flashSize: 'keep',
        eraseAll: false, compress: true,
        calculateMD5Hash: md5,
        reportProgress: (i, written, totalComp) => {
          const before = sizes.slice(0, i).reduce((a, b) => a + b, 0);
          const pct = Math.min(100, Math.floor(((before + sizes[i] * (totalComp ? written / totalComp : 1)) / total) * 100));
          if (pct === last) return;
          last = pct;
          ui.bar.pct = pct;
          setSub(stepId, { key: 'sub.writing', vars: { pct } });
        },
      });
    } catch (e) {
      throw new FlashError('lost', e.message);
    }
    log('log.writeDone', {}, 'ok');
  }

  async function detectAndFix(port, env) {
    setActive('detect');
    const r = await detectPanel(port);
    if (!r) {
      log('log.noPanel', { sec: 7 }, 'err');
      setDone('detect', { key: 'sub.panelUnknownAfter', warn: true });
      return { panel: null, env };
    }
    log('log.panel', r);
    const want = PANEL_ENV[r.panel] || env;
    if (want === env) {
      setDone('detect', { key: 'sub.panelOk', vars: { panel: r.panel } });
      return { panel: r.panel, env };
    }
    setDone('detect', { key: 'sub.panelOther', warn: true });
    insertBefore('done', 'rewrite');
    setActive('rewrite');
    const ld = await connectLoader(port);
    await writeBuild(ld, want, 'rewrite');
    await closeLoader(ld);
    setDone('rewrite', { key: 'sub.panel', vars: { panel: r.panel, env: want } });
    return { panel: r.panel, env: want };
  }

  function selectedMode() {
    const c = el.modes.querySelector('input:checked');
    return c ? c.value : 'new';
  }

  async function start() {
    if (busy || !supported) return;
    if (!manifest) await loadManifest();
    if (!manifest) { showNoRelease(); return; }
    let port;
    try {
      const filters = showAllPorts ? [] : Object.keys(USB_CHIPS).map((v) => ({ usbVendorId: Number(v) }));
      port = await navigator.serial.requestPort({ filters });
    } catch (e) {
      if (e && e.name === 'NotFoundError') {
        fail(new FlashError('noPort'), 'port', selectedMode(), 0);
        return;
      }
      fail(e, 'port', selectedMode(), 0);
      return;
    }
    await run(port, selectedMode());
  }

  async function run(port, mode) {
    busy = true;
    await refreshManifest();
    raw = [`CYD Deck web flasher · firmware v${manifest.version} · mode ${mode} · ${navigator.userAgent}`];
    ui.status = 'busy';
    ui.result = null;
    ui.bar = null;
    ui.log = [];
    ui.chip = { raw: portName(port) };
    ui.steps = (mode === 'new' ? ['connect', 'erase', 'write', 'detect', 'done'] : ['connect', 'read', 'write', 'done'])
      .map((id) => ({ id, state: 'pending' }));
    const started = performance.now();
    let wake = null;
    try { wake = await navigator.wakeLock.request('screen'); } catch {}

    let stage = 'connect';
    let panel = null;
    let env = 'cyd';
    try {
      setActive('connect');
      await fetchBuild('cyd');
      await fetchBuild('cyd2usb').catch(() => null);

      if (mode === 'new') {
        const ld = await connectLoader(port);
        setDone('connect', { key: 'sub.chip', vars: { chip: ld.chip, flash: ld.flash } });
        stage = 'erase';
        setActive('erase');
        log('log.erase');
        try { await ld.loader.eraseFlash(); } catch (e) { throw new FlashError('lost', e.message); }
        log('log.eraseDone', {}, 'ok');
        setDone('erase');
        stage = 'write';
        setActive('write');
        setSub('write', { key: 'sub.write', vars: { env: 'cyd', panel: 'ILI9341' } });
        await writeBuild(ld, 'cyd', 'write');
        await closeLoader(ld);
        setDone('write', { key: 'sub.write', vars: { env: 'cyd', panel: 'ILI9341' } });
        stage = 'detect';
        ({ panel, env } = await detectAndFix(port, 'cyd'));
      } else {
        await openPort(port, BAUD_APP);
        log('log.open', { port: portName(port) });
        setDone('connect', { key: 'sub.port', vars: { port: portName(port) } });
        stage = 'read';
        setActive('read');
        log('log.reset');
        let r;
        try { r = await readPanel(port); } finally { await closePort(port); }
        if (r) {
          log('log.panel', r);
          panel = r.panel;
          env = PANEL_ENV[panel] || 'cyd';
          setDone('read', { key: 'sub.panel', vars: { panel, env } });
        } else {
          log('log.noPanel', { sec: 7 }, 'err');
          setDone('read', { key: 'sub.panelUnknown', warn: true });
          insertBefore('done', 'detect');
        }
        stage = 'write';
        setActive('write');
        setSub('write', { key: 'sub.write', vars: { env, panel: panel || 'ILI9341' } });
        const ld = await connectLoader(port);
        await writeBuild(ld, env, 'write');
        await closeLoader(ld);
        setDone('write', { key: 'sub.write', vars: { env, panel: panel || 'ILI9341' } });
        if (!r) { stage = 'detect'; ({ panel, env } = await detectAndFix(port, env)); }
      }

      setDone('done');
      log('log.ready', {}, 'ok');
      ui.status = 'done';
      ui.bar = null;
      const seconds = Math.round((performance.now() - started) / 1000);
      ui.result = {
        kind: 'ok',
        title: 'ok.title',
        text: panel ? 'ok.text' : 'ok.textUnknown',
        extra: mode === 'update' ? 'ok.reconnect' : null,
        vars: { version: manifest.version, panel, env },
        actions: [{ label: 'ok.agent', href: '#agent', cls: 'btn-light' }],
      };
      stats.report('ok', { mode, panel, env, version: manifest.version, seconds });
    } catch (e) {
      const seconds = Math.round((performance.now() - started) / 1000);
      const s = step(stage);
      if (s) s.state = 'failed';
      fail(e, stage, mode, seconds);
    } finally {
      busy = false;
      try { if (wake) await wake.release(); } catch {}
      try { await port.close(); } catch {}
      render();
    }
  }

  function fail(e, stage, mode, seconds) {
    const kind = e instanceof FlashError ? e.kind : classify(e);
    if (e && e.message) rawLine(`[error] ${e.message}`);
    busy = false;
    ui.status = kind === 'noPort' ? 'idle' : 'error';
    ui.bar = null;
    const retry = { label: 'err.retry', onClick: start, cls: 'btn-primary' };
    const results = {
      noPort: { kind: 'warn', title: 'err.noPort.title', text: 'err.noPort.text', actions: [
        retry,
        { label: 'err.noPort.all', onClick: () => { showAllPorts = true; start(); } },
        { label: 'err.noPort.driver', href: DRIVER_URL },
      ] },
      busy: { kind: 'err', title: 'err.busy.title', text: 'err.busy.text', actions: [retry] },
      sync: { kind: 'err', title: 'err.sync.title', text: 'err.sync.text', actions: [retry] },
      lost: { kind: 'err', title: 'err.lost.title', text: 'err.lost.text', actions: [retry] },
      download: { kind: 'err', title: 'err.download.title', text: 'err.download.text', actions: [retry] },
      other: { kind: 'err', title: 'err.other.title', text: 'err.other.text', actions: [
        retry, { label: 'err.report', href: `${repo}/issues/new` },
      ] },
    };
    ui.result = { ...(results[kind] || results.other), vars: { message: (e && e.message) || String(e) } };
    if (ui.log.length && ui.log[ui.log.length - 1].cls === 'now') ui.log[ui.log.length - 1].cls = 'err';
    stats.report('fail', { mode, stage, error: kind, version: manifest && manifest.version, seconds });
    render();
  }

  function classify(e) {
    const msg = `${e && e.name} ${e && e.message}`;
    if (/Failed to open serial port/i.test(msg)) return 'busy';
    if (/lost|disconnect|NetworkError|BreakError|FramingError|BufferOverrun/i.test(msg)) return 'lost';
    return 'other';
  }

  function showUnsupported() {
    ui.status = 'idle';
    ui.chip = { key: 'chip.none' };
    const copy = {
      label: 'err.unsupported.copy',
      cls: 'btn-ghost',
      onClick: async () => {
        try { await navigator.clipboard.writeText(location.href.split('#')[0]); copy.done = 'err.unsupported.copied'; render(); } catch {}
      },
    };
    ui.result = { kind: 'warn', title: 'err.unsupported.title', text: 'err.unsupported.text', actions: [copy] };
    render();
  }

  function showNoRelease() {
    ui.result = {
      kind: 'warn', title: 'err.noRelease.title', text: 'err.noRelease.text',
      actions: [{ label: 'err.noRelease.open', href: `${repo}/releases` }],
    };
    render();
  }

  function resetSteps() {
    const mode = selectedMode();
    ui.steps = (mode === 'new' ? ['connect', 'erase', 'write', 'detect', 'done'] : ['connect', 'read', 'write', 'done'])
      .map((id) => ({ id, state: 'pending' }));
  }

  el.start.addEventListener('click', start);
  el.modes.addEventListener('change', () => {
    if (busy) return;
    resetSteps();
    if (ui.status !== 'idle' && ui.result && ui.result.kind !== 'warn') { ui.status = 'idle'; ui.result = null; }
    render();
  });
  el.logCopy.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(raw.join('\n'));
      const old = el.logCopy.textContent;
      el.logCopy.textContent = tr('copied');
      setTimeout(() => { el.logCopy.textContent = old; }, 2000);
    } catch {}
  });
  window.addEventListener('beforeunload', (e) => {
    if (!busy) return;
    e.preventDefault();
    e.returnValue = tr('leave');
  });
  onLangChange(render);

  resetSteps();
  render();
  if (!supported) showUnsupported();
  loadManifest().then(() => { if (supported && !manifest) showNoRelease(); render(); });
}
