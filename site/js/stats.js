let gcLoaded = false;

function loadGoatCounter(url) {
  if (gcLoaded || !url) return;
  gcLoaded = true;
  const s = document.createElement('script');
  s.async = true;
  s.src = 'https://gc.zgo.at/count.js';
  s.dataset.goatcounter = url;
  document.head.append(s);
}

function browserName() {
  const ua = navigator.userAgent;
  if (/Edg\//.test(ua)) return 'edge';
  if (/OPR\//.test(ua)) return 'opera';
  if (/Chrome\//.test(ua)) return 'chrome';
  if (/Firefox\//.test(ua)) return 'firefox';
  if (/Safari\//.test(ua)) return 'safari';
  return 'other';
}

function osName() {
  const p = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '';
  if (/win/i.test(p)) return 'windows';
  if (/mac/i.test(p)) return 'macos';
  if (/linux/i.test(p)) return 'linux';
  if (/android/i.test(navigator.userAgent)) return 'android';
  return 'other';
}

export function initStats(config) {
  const s = (config && config.stats) || {};
  loadGoatCounter(s.goatcounter);
  return {
    report(result, info) {
      const data = {
        result,
        mode: info.mode,
        panel: info.panel || 'unknown',
        env: info.env || '',
        version: info.version || '',
        stage: info.stage || '',
        error: info.error || '',
        seconds: info.seconds || 0,
        browser: browserName(),
        os: osName(),
        lang: document.documentElement.lang,
      };
      if (s.goatcounter) {
        const path = result === 'ok'
          ? `flash/ok/${data.mode}/${data.panel}`
          : `flash/fail/${data.stage || 'unknown'}/${data.error || 'other'}`;
        const send = () => window.goatcounter && window.goatcounter.count && window.goatcounter.count({
          path, title: `v${data.version} ${data.env} ${data.seconds}s`, event: true,
        });
        if (window.goatcounter && window.goatcounter.count) send(); else setTimeout(send, 1500);
      }
      if (s.endpoint) {
        try {
          navigator.sendBeacon(s.endpoint, new Blob([JSON.stringify(data)], { type: 'text/plain' }));
        } catch {}
      }
    },
  };
}
