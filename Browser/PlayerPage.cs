// The full-bleed shaka-player page served by LocalServer at /player. No visible controls: the WPF host draws them.
// NOTE: the JS below deliberately contains no double quotes (C# verbatim string).
using System;
using System.Text;

namespace SeleneBrowser
{
    public static class PlayerPage
    {
        const string Html = @"<!doctype html>
<html><head><meta charset='utf-8'><title>Lanternfall Player</title>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
html,body{margin:0;height:100%;background:#000;overflow:hidden;cursor:none;user-select:none}
#v{position:absolute;left:0;top:0;width:100%;height:100%;background:#000;object-fit:contain;outline:none}
#tc{position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none;font-family:Segoe UI,Arial,sans-serif;font-size:3.6vh}
/* captions, TV style (shaka's own stylesheet is not loaded): every cue stacked bottom-centre, white text on a dark box.
   YouTube's cues carry positions meant for a small web player, so they are flattened into this one layout. */
#tc .shaka-text-container{position:absolute!important;left:0!important;top:0!important;right:0!important;bottom:0!important;width:auto!important;height:auto!important;
 display:flex!important;flex-direction:column!important;justify-content:flex-end!important;align-items:center!important;padding:0 6vw 7vh!important;box-sizing:border-box!important}
#tc .shaka-text-container div{position:static!important;top:auto!important;left:auto!important;right:auto!important;bottom:auto!important;width:auto!important;height:auto!important;
 max-width:88vw!important;margin:0!important;padding:0!important;display:block!important;text-align:center!important;background:transparent!important;transform:none!important;
 color:#fff!important;font-size:3.6vh!important;text-shadow:0 0 .15em #000,0 0 .3em #000!important}
/* Plain-text cues: Shaka puts the whole line in one text span, so the box belongs there. Word-level cues are nested
   spans: paint one box on their line wrapper instead of a separate pill per word. float:none keeps that inline
   wrapper tight around the words when YouTube's align:start setting makes Shaka float the nested cues. */
#tc .shaka-text-container span{color:#fff!important;background:none!important;padding:0!important;float:none!important;font-size:3.6vh!important;line-height:1.45!important;font-family:Segoe UI,Arial,sans-serif!important;
 white-space:pre-wrap!important;text-shadow:none!important}
#tc .shaka-text-container span[style*=background-color],
#tc .shaka-text-container .shaka-text-wrapper{background:rgba(0,0,0,.72)!important;padding:.06em .4em!important;border-radius:.2em!important;-webkit-box-decoration-break:clone;box-decoration-break:clone}
#tc .shaka-text-container .shaka-text-wrapper span[style*=background-color]{background:transparent!important;padding:0!important}
</style>
<!--NOMAD_LIB-->
</head><body>
<video id='v' autoplay playsinline></video><div id='tc'></div>
<script>
var CFG = /*NOMAD_CFG*/;
(function(){
  var video = document.getElementById('v');
  var player = null;
  var S = { err: CFG.error || '', buffering: true, caption: '', rate: 1, auto: true, cap: CFG.maxHeight || 0, loaded: {}, segs: [], skipped: 0, lastSkip: 0, ready: false };

  function minHeight(){
    var m = 1e9, tr = player ? player.getVariantTracks() : [];
    for (var i = 0; i < tr.length; i++) if (tr[i].height && tr[i].height < m) m = tr[i].height;
    return m;
  }
  // Auto quality = ABR between a floor and the cap. The floor (720p, or the best rendition under the cap when the
  // video has nothing that high) keeps a seek's slow first range request from dropping a TV picture to 144p.
  // The cap hides the renditions above it from shaka, so the menu's list is the one taken at load, before the cap.
  function applyCap(){
    if (!player) return;
    // the full list, taken before any cap hides renditions (the first call comes while the stream is still loading)
    var all = heights();
    if (!S.allHeights || all.length > S.allHeights.length) S.allHeights = all;
    var cap = S.auto && S.cap > 0 && minHeight() <= S.cap ? S.cap : Infinity;   // a picked quality is never capped back
    var floor = 0, hs = heights();
    for (var i = 0; i < hs.length; i++) if (hs[i] <= cap && hs[i] <= 720) { floor = hs[i]; break; }
    try { player.configure({ restrictions: { maxHeight: cap }, abr: { restrictions: { minHeight: floor } } }); } catch (e) {}
  }
  function activeTrack(){
    var tr = player ? player.getVariantTracks() : [];
    for (var i = 0; i < tr.length; i++) if (tr[i].active) return tr[i];
    return null;
  }
  function heights(){
    var tr = player ? player.getVariantTracks() : [], seen = {}, out = [];
    for (var i = 0; i < tr.length; i++) { var h = tr[i].height; if (h && !seen[h]) { seen[h] = 1; out.push(h); } }
    out.sort(function(a, b){ return b - a; });
    return out;
  }
  function curLang(){
    var a = activeTrack();
    return a ? a.language : '';
  }

  // The first codec candidate (AV1, then VP9 webm, then H.264; see LocalServer.PlayableVideo) this engine can decode,
  // asked with the stream's own codec string, size and frame rate. AV1 only in hardware (power efficient): software AV1
  // at 4K stutters. H.264, the last, is taken without asking.
  function pickManifest(){
    var url = location.origin + CFG.manifest, list = CFG.cands || [];
    function use(c){ S.codec = c ? c.name : 'vp9'; return url + (c ? c.suffix : ''); }
    if (!list.length) return Promise.resolve(use(null));
    if (!navigator.mediaCapabilities) return Promise.resolve(use(list.filter(function(c){ return c.name !== 'av1'; })[0] || list[0]));
    function at(i){
      var c = list[i];
      if (i >= list.length - 1) return Promise.resolve(use(c));
      return navigator.mediaCapabilities.decodingInfo({ type: 'media-source',
        video: { contentType: c.type, width: c.w, height: c.h, bitrate: c.rate, framerate: c.fps } })
        .then(function(r){ return r && r.supported && (c.name !== 'av1' || r.powerEfficient) ? use(c) : at(i + 1); }, function(){ return at(i + 1); });
    }
    return at(0);
  }

  // Recovery of a critical error while (or before) playing: up to 3 attempts at 1 s, 3 s, 6 s. A network/streaming/media error first
  // tries shaka's retryStreaming(); an expired url (403/404/410) or a later attempt reloads the manifest at the current position with
  // fresh=1 (the server fetches new googlevideo urls). The counter resets after 30 s of smooth playback. S.err is set only when
  // recovery fails and the picture is not moving.
  var rec = { n: 0, busy: false, t: -1, at: 0, smooth: 0 };
  function moving(){ return rec.at && Date.now() - rec.at < 3500 && !video.paused; }
  function failPlayback(msg){
    if (moving()) return;
    S.err = msg || 'Could not keep playing this video.';
  }
  setInterval(function(){
    var t = video.currentTime, now = Date.now();
    if (!video.paused && t !== rec.t && !video.seeking) {
      rec.at = now;
      if (!rec.smooth) rec.smooth = now; else if (now - rec.smooth > 30000) rec.n = 0;
    } else if (!video.paused && !S.buffering) rec.smooth = 0;
    rec.t = t;
  }, 1000);
  function freshUrl(){ return S.url + (S.url.indexOf('?') < 0 ? '?' : '&') + 'fresh=1&_=' + Date.now(); }
  function recover(d){
    d = d || {};
    var cat = d.category, retryable = cat === 1 || cat === 3 || cat === 4 || cat === 5 || !d.code;
    if (!player || !S.url || !retryable || CFG.live) { failPlayback(); return; }
    if (rec.busy) return;
    if (rec.n >= 3) { failPlayback(); return; }
    var status = d.data && d.data.length > 1 ? d.data[1] : 0, expired = status === 403 || status === 404 || status === 410;
    var delay = [1000, 3000, 6000][rec.n++], t = video.currentTime || CFG.start || 0;
    rec.busy = true;
    setTimeout(function(){
      try {
        if (!expired && rec.n === 1 && cat !== 4 && S.ready && player.retryStreaming && player.retryStreaming()) { rec.busy = false; return; }
      } catch (x) {}
      S.buffering = true;
      player.load(freshUrl(), t > 0 ? t : null).then(function(){
        rec.busy = false; S.ready = true; S.err = ''; applyCap();
        if (wantPlay) { var p = video.play(); if (p && p.catch) p.catch(function(){}); }
      }, function(){ rec.busy = false; recover(d); });
    }, delay);
  }

  function init(){
    if (CFG.error) return;
    if (!window.shaka) { S.err = 'Could not load the player library. Check your internet connection.'; return; }
    shaka.polyfill.installAll();
    if (!shaka.Player.isBrowserSupported()) { S.err = 'This browser cannot play this video format.'; return; }
    player = new shaka.Player();
    player.attach(video).then(function(){
      try {
        player.configure({ textDisplayFactory: function(){ return new shaka.text.UITextDisplayer(video, document.getElementById('tc')); } });
      } catch (e) {}
      // No overall request timeout: one 4K VP9 subsegment can be ~180 MB, and shaka's default 30 s limit threw away a slow
      // but healthy download and started it over (endless buffering mid-video). A request with no bytes for 15 s still fails.
      var rp = { maxAttempts: 5, baseDelay: 800, backoffFactor: 2, timeout: 0, stallTimeout: 15000, connectionTimeout: 15000 };
      player.configure({
        abr: { enabled: true, defaultBandwidthEstimate: 25000000 },
        // the manifest answer waits for the stream lookup (a fresh=1 reload can take tens of seconds): no stall limit there
        manifest: { retryParameters: { maxAttempts: 3, baseDelay: 1000, backoffFactor: 2, timeout: 90000, stallTimeout: 0, connectionTimeout: 0 } },
        streaming: { bufferingGoal: 45, rebufferingGoal: 2, bufferBehind: 30, retryParameters: rp }
      });
      // only CRITICAL (severity 2) errors matter; recoverable ones (1) are retried by shaka itself and must not touch the UI
      player.addEventListener('error', function(e){
        var d = e.detail || {};
        S.dbg = 'shaka ' + d.code + ' sev=' + d.severity; try { console.log('shaka error', d.code, d.severity, d.category, d.data); } catch (x) {}
        if (d.severity !== 2) return;
        recover(d);
      });
      player.addEventListener('buffering', function(e){ S.buffering = !!e.buffering; });
      player.addEventListener('trackschanged', applyCap);
      return pickManifest().then(function(url){ S.url = url; return player.load(url, CFG.start > 0 ? CFG.start : null); });
    }).then(function(){
      S.ready = true;
      applyCap();
      var def = '';
      for (var i = 0; i < (CFG.audios || []).length; i++) if (CFG.audios[i].isDefault) def = CFG.audios[i].lang;
      if (def) { try { player.selectAudioLanguage(def); } catch (e) {} }
      var p = video.play();
      if (p && p.catch) p.catch(function(){});
    }).catch(function(e){
      var d = (e && e.detail) || e || {};
      try { console.log('shaka load error', d.code, d.category); } catch (x) {}
      if (S.url && (d.category === 1 || d.category === 4) && rec.n < 3) { recover(d); return; }
      S.err = 'Could not load the video.';
    });
  }
  video.addEventListener('error', function(){
    if (CFG.seg || CFG.live) return;
    var er = video.error;
    S.dbg = 'video error ' + (er ? er.code : '?');
    if (S.ready) recover({ category: 3, code: 3000 });
  });

  video.addEventListener('waiting', function(){ S.buffering = true; });
  video.addEventListener('playing', function(){ S.buffering = false; });
  video.addEventListener('canplay', function(){ S.buffering = false; });

  if (CFG.sponsor) {
    fetch('/api/sponsor?v=' + encodeURIComponent(CFG.id)).then(function(r){ return r.json(); }).then(function(a){ S.segs = a || []; }).catch(function(){});
  }
  setInterval(function(){
    if (!S.segs.length || video.paused) return;
    var t = video.currentTime, now = Date.now();
    for (var i = 0; i < S.segs.length; i++) {
      var g = S.segs[i];
      if (t >= g.s && t < g.e - 0.4 && now - S.lastSkip > 1200) {
        S.lastSkip = now; S.skipped++;
        video.currentTime = g.e;
        break;
      }
    }
  }, 250);

  // ---- live: hls.js on the same window.nomad API (state() adds live, behind = seconds behind the live edge, ws = window start)
  var hls = null, hlsNetRetries = 0, hlsMediaRetries = 0;
  function liveStart(){ try { return video.seekable.length ? video.seekable.start(0) : 0; } catch (e) { return 0; } }
  function liveEdge(){ try { return video.seekable.length ? video.seekable.end(video.seekable.length - 1) : (video.currentTime || 0); } catch (e) { return 0; } }
  function liveTarget(){
    var e = liveEdge(), p = hls ? hls.liveSyncPosition : 0;
    return p && p > 0 && p <= e + 0.5 ? p : Math.max(0, e - 3);
  }
  function liveLevel(){ return hls && hls.levels && hls.currentLevel >= 0 ? hls.levels[hls.currentLevel] : null; }
  function liveHeights(){
    var lv = hls && hls.levels ? hls.levels : [], seen = {}, out = [];
    for (var i = 0; i < lv.length; i++) { var h = lv[i].height; if (h && !seen[h]) { seen[h] = 1; out.push(h); } }
    out.sort(function(a, b){ return b - a; });
    return out;
  }
  function liveCap(){
    if (!hls || !hls.levels) return;
    var best = -1;
    if (S.cap > 0) {
      for (var i = 0; i < hls.levels.length; i++) { var h = hls.levels[i].height || 0; if (h && h <= S.cap && (best < 0 || h >= (hls.levels[best].height || 0))) best = i; }
    }
    try { hls.autoLevelCapping = best; } catch (e) {}
  }
  function liveQuality(h){
    if (!hls) return;
    if (h === 'auto' || !h) { S.auto = true; hls.currentLevel = -1; liveCap(); return; }
    h = parseInt(h, 10);
    var best = -1;
    for (var i = 0; i < hls.levels.length; i++) {
      if (hls.levels[i].height !== h) continue;
      if (best < 0 || (hls.levels[i].bitrate || 0) > (hls.levels[best].bitrate || 0)) best = i;
    }
    if (best >= 0) { S.auto = false; hls.currentLevel = best; }
  }
  function liveState(){
    var lv = liveLevel(), t = video.currentTime || 0;
    return JSON.stringify({
      t: t, d: liveEdge(), ws: liveStart(), live: true, behind: Math.max(0, liveTarget() - t),
      paused: video.paused, buffering: !!S.buffering && !video.paused || (S.buffering && !S.ready),
      ended: false, quality: S.auto ? 'auto' : (lv ? lv.height : 'auto'), height: lv ? (lv.height || 0) : 0, qualities: liveHeights(),
      captions: [], caption: '', audios: [], audio: '', rate: S.rate, error: S.err, skipped: 0, ready: S.ready,
      dbg: 'rs=' + video.readyState + ' ns=' + video.networkState + ' buf=' + (video.buffered.length ? video.buffered.start(0).toFixed(1) + '-' + video.buffered.end(video.buffered.length - 1).toFixed(1) : 'none')
        + ' seek=' + liveStart().toFixed(1) + '-' + liveEdge().toFixed(1) + ' lv=' + (hls ? hls.levels.length + '/' + hls.currentLevel : '-') + ' vis=' + document.visibilityState
    });
  }
  function initLive(){
    if (CFG.error) return;
    if (!window.Hls) { S.err = 'Could not load the live player library. Check your internet connection.'; return; }
    if (!Hls.isSupported()) { S.err = 'This browser cannot play live streams.'; return; }
    hls = new Hls({ enableWorker: true, lowLatencyMode: false, liveSyncDurationCount: 3, maxBufferLength: 30, backBufferLength: 300,
      manifestLoadingMaxRetry: 4, levelLoadingMaxRetry: 6, fragLoadingMaxRetry: 6 });
    hls.on(Hls.Events.MANIFEST_PARSED, function(){
      S.ready = true;
      liveCap();
      var p = video.play();
      if (p && p.catch) p.catch(function(){});
    });
    hls.on(Hls.Events.FRAG_LOADED, function(){ hlsNetRetries = 0; });
    hls.on(Hls.Events.ERROR, function(ev, data){
      if (!data || !data.fatal) return;
      if (data.type === Hls.ErrorTypes.NETWORK_ERROR && ++hlsNetRetries <= 6) { setTimeout(function(){ try { hls.startLoad(); } catch (e) {} }, 1500); return; }
      if (data.type === Hls.ErrorTypes.MEDIA_ERROR && ++hlsMediaRetries <= 3) { try { hls.recoverMediaError(); } catch (e) {} return; }
      S.err = 'Could not keep playing this live stream.'; S.dbg = 'hls ' + data.details;
    });
    hls.attachMedia(video);
    hls.loadSource(location.origin + CFG.hls);
  }

/*NOMAD_SEGJS*/
  function seekTo(s){
    if (CFG.seg) { segSeekTo(s); return; }
    if (CFG.live) {
      var st = liveStart(), ed = liveEdge();
      video.currentTime = Math.max(st + 0.5, Math.min(s, Math.max(st + 0.5, ed - 1)));
      return;
    }
    var d = isFinite(video.duration) ? video.duration : (CFG.duration || 0);
    s = Math.max(0, d > 0 ? Math.min(s, d - 0.3) : s);
    video.currentTime = s;
  }

  // A video starts playing and keeps playing until the user pauses it (A, or the overlay closing). A start play() the
  // browser refused or dropped (autoplay before any input, a play() cut short by the stream reloading) is retried here,
  // and on the first click (the host clicks the page once it loads), instead of leaving the video paused.
  var wantPlay = true;
  function retryPlay(){ if (wantPlay && S.ready && video.paused && !video.ended && !S.err) { var p = video.play(); if (p && p.catch) p.catch(function(){}); } }
  setInterval(retryPlay, 700);
  document.addEventListener('pointerdown', retryPlay, true);
  document.addEventListener('mousedown', retryPlay, true);

  window.nomad = {
    play: function(){ wantPlay = true; var p = video.play(); if (p && p.catch) p.catch(function(){}); },
    pause: function(){ wantPlay = false; video.pause(); },
    // leaving the player: nothing may keep playing, even if the host's navigation away never lands
    stop: function(){
      wantPlay = false; S.ready = false;
      try { video.pause(); } catch (e) {}
      try { if (player) player.unload(); } catch (e) {}
      try { if (hls) hls.destroy(); } catch (e) {}
      try { video.removeAttribute('src'); video.load(); } catch (e) {}
    },
    toggle: function(){ if (video.paused) window.nomad.play(); else window.nomad.pause(); },
    seekBy: function(s){ seekTo(video.currentTime + s); },
    seekTo: function(s){ seekTo(s); },
    setRate: function(r){ S.rate = r; video.playbackRate = r; },
    goLive: function(){ if (CFG.seg) { if (SG && SG.opened) segGoLive(); window.nomad.play(); return; } if (!CFG.live) return; video.currentTime = liveTarget(); window.nomad.play(); },
    setQuality: function(h){
      if (CFG.seg) { segQuality(h); return; }
      if (CFG.live) { liveQuality(h); return; }
      if (!player) return;
      if (h === 'auto' || !h) {
        S.auto = true;
        player.configure({ abr: { enabled: true } });
        applyCap();
        return;
      }
      S.auto = false;
      h = parseInt(h, 10);
      try { player.configure({ restrictions: { maxHeight: Infinity }, abr: { enabled: false } }); } catch (e) {}
      var lang = curLang(), best = null, tr = player.getVariantTracks();
      for (var i = 0; i < tr.length; i++) {
        var t = tr[i];
        if (t.height !== h) continue;
        if (lang && t.language !== lang && best && best.language === lang) continue;
        if (!best || (t.language === lang && best.language !== lang) || ((t.language === best.language) && t.bandwidth > best.bandwidth)) best = t;
      }
      if (best) player.selectVariantTrack(best, true);
    },
    setAudio: function(lang){
      if (!player) return;
      try { player.selectAudioLanguage(lang); } catch (e) {}
    },
    setCaption: function(lang){
      if (!player) return;
      if (!lang) { S.caption = ''; try { player.setTextTrackVisibility(false); } catch (e) {} return; }
      var c = null;
      for (var i = 0; i < (CFG.captions || []).length; i++) if (CFG.captions[i].lang === lang) { c = CFG.captions[i]; break; }
      if (!c) return;
      S.caption = lang;
      function show(){
        try { player.selectTextLanguage(lang); player.setTextTrackVisibility(true); } catch (e) {}
      }
      if (S.loaded[lang]) { show(); return; }
      player.addTextTrackAsync(location.origin + c.url, lang, 'subtitles', 'text/vtt', '', c.label).then(function(){ S.loaded[lang] = 1; show(); }).catch(function(){ S.caption = ''; });
    },
    state: function(){
      if (CFG.seg) return segState();
      if (CFG.live) return liveState();
      var a = activeTrack();
      var d = isFinite(video.duration) ? video.duration : (CFG.duration || 0);
      var q = S.auto ? 'auto' : (a ? a.height : 'auto');
      return JSON.stringify({
        t: video.currentTime || 0, d: d, paused: video.paused, buffering: !!S.buffering && !video.paused || (S.buffering && !S.ready),
        ended: video.ended, quality: q, height: a ? a.height : 0, qualities: S.allHeights || heights(),
        captions: (CFG.captions || []).map(function(c){ return { lang: c.lang, label: c.label }; }), caption: S.caption,
        audios: (CFG.audios || []).map(function(c){ return { lang: c.lang, label: c.label }; }), audio: curLang(),
        rate: S.rate, error: S.err, skipped: S.skipped, ready: S.ready,
        dbg: 'rs=' + video.readyState + ' ns=' + video.networkState + ' buf=' + (video.buffered.length ? video.buffered.start(0).toFixed(1) + '-' + video.buffered.end(video.buffered.length - 1).toFixed(1) : 'none')
          + ' vis=' + document.visibilityState + ' rate=' + video.playbackRate + ' codec=' + (S.codec || '?') + ' pic=' + video.videoWidth + 'x' + video.videoHeight + ' offered=' + (S.allHeights || []).join('/')
      });
    }
  };

  document.addEventListener('contextmenu', function(e){ e.preventDefault(); });
  document.addEventListener('keydown', function(e){ if (e.key === ' ' || e.key.indexOf('Arrow') === 0) e.preventDefault(); }, true);
  if (CFG.seg) initSeg(); else if (CFG.live) initLive(); else init();
})();
</script></body></html>";

        const string ShakaTag = "<script src='https://cdn.jsdelivr.net/npm/shaka-player@4.11.7/dist/shaka-player.compiled.js'></script>";
        const string HlsTag = "<script src='https://cdn.jsdelivr.net/npm/hls.js@1.5.17/dist/hls.min.js'></script>";

        public static string Build(string cfgJson) { return Build(cfgJson, "dash"); }

        /// <summary>live = hls.js page (CFG.hls playlist), otherwise the shaka DASH page.</summary>
        public static string Build(string cfgJson, bool live) { return Build(cfgJson, live ? "hls" : "dash"); }

        /// <summary>mode: "dash" = shaka, "hls" = hls.js live, "seg" = live segments through the built-in MSE player (no library).</summary>
        public static string Build(string cfgJson, string mode)
        {
            string safe = cfgJson.Replace("</", "<\\/").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
            string lib = mode == "seg" ? "" : (mode == "hls" ? HlsTag : ShakaTag);
            return Html.Replace("<!--NOMAD_LIB-->", lib).Replace("/*NOMAD_CFG*/", safe).Replace("/*NOMAD_SEGJS*/", SegJs);
        }

        // Live segment player (MSE). One video + one audio SourceBuffer, fed by two lock-stepped loops that walk the
        // sq numbers of /lseg/<token>. No double quotes in here (C# verbatim string); mime strings come from the config.
        const string SegJs = @"
  var SG = null;
  function segRange(sb){
    try { if (sb && sb.buffered.length) return { s: sb.buffered.start(0), e: sb.buffered.end(sb.buffered.length - 1) }; } catch (e) {}
    return null;
  }
  function segLadder(list){
    var by = {}, out = [], i, k;
    var canChange = !!(window.SourceBuffer && SourceBuffer.prototype.changeType);
    var firstMime = '';
    for (i = 0; i < list.length; i++) {
      var e = list[i];
      if (!window.MediaSource || !MediaSource.isTypeSupported(e.mime)) continue;
      var c = by[e.height];
      if (!c || e.fps > c.fps || (e.fps === c.fps && e.webm && !c.webm)) by[e.height] = e;
    }
    for (k in by) out.push(by[k]);
    out.sort(function(a, b){ return a.height - b.height; });
    if (!canChange && out.length) {   // no changeType: stay inside the mime of the best entry
      firstMime = out[out.length - 1].mime;
      out = out.filter(function(e){ return e.mime === firstMime; });
    }
    return out;
  }
  function segCapIdx(){
    var idx = 0, cap = S.cap > 0 ? S.cap : 1e9;
    for (var i = 0; i < SG.ladder.length; i++) if (SG.ladder[i].height <= cap) idx = i;
    return idx;
  }
  // stream time (s) of the start of segment sq, from the newest head header pair (timestamps inside segments are absolute)
  function segTime(sq){ return SG.headTime / 1000 - (SG.head - sq) * SG.seg; }
  function segHeadEst(){
    var el = (performance.now() - SG.headAt) / 1000;
    return SG.head + Math.min(1, el / SG.seg);
  }
  function segLiveT(){ return SG.head > 0 ? segTime(segHeadEst() - 2) : 0; }
  function segWinStart(){ return SG.head > 0 ? segTime(SG.head - 720) : 0; }
  function segCurSq(){ return SG.head > 0 ? SG.head - (SG.headTime / 1000 - (video.currentTime || 0)) / SG.seg : 0; }

  function segFetch(tok, sq, gen, probe){
    var t0 = performance.now();
    var opt = { cache: 'no-store' };
    var ctl = null;
    if (window.AbortController && !probe) { ctl = new AbortController(); opt.signal = ctl.signal; SG.ctl.push(ctl); }
    return fetch('/lseg/' + tok + (sq === null ? '' : '?sq=' + sq), opt).then(function(r){
      var h = parseInt(r.headers.get('X-Head-Seqnum') || '0', 10), ht = parseInt(r.headers.get('X-Head-Time-Millis') || '0', 10);
      if (h > 0 && ht > 0 && h >= SG.head) { SG.head = h; SG.headTime = ht; SG.headAt = performance.now(); }
      if (r.status !== 200) { try { if (r.body) r.body.cancel(); } catch (e) {} return { status: r.status }; }
      if (probe) { try { if (r.body) r.body.cancel(); } catch (e) {} return { status: 200 }; }
      return r.arrayBuffer().then(function(b){ return { status: 200, buf: b, ms: performance.now() - t0, t1: performance.now() }; });
    });
  }
  // serialised SourceBuffer operations; fn starts one operation that ends with updateend (return false = nothing started)
  function segRun(kind, gen, fn){
    var p = SG.chain[kind].then(function(){
      var sb = SG.sb[kind];
      if (!sb || (gen !== undefined && gen !== SG.gen)) return;
      return new Promise(function(res, rej){
        function done(){ sb.removeEventListener('updateend', done); res(); }
        sb.addEventListener('updateend', done);
        var started;
        try { started = fn(sb); } catch (x) { sb.removeEventListener('updateend', done); rej(x); return; }
        if (started === false) { sb.removeEventListener('updateend', done); res(); }
      });
    });
    SG.chain[kind] = p.catch(function(){});
    return p;
  }
  function segAppend(kind, e, buf, gen){
    return segRun(kind, gen, function(sb){
      if (kind === 'v' && e.mime !== SG.vMime) { sb.changeType(e.mime); SG.vMime = e.mime; }
      sb.appendBuffer(buf);
    }).catch(function(x){
      if (gen !== SG.gen) return;
      if (x && x.name === 'QuotaExceededError') return segTrim(10).then(function(){ return segAppend(kind, e, buf, gen); });
      throw x;
    });
  }
  function segTrim(keep){
    var ct = video.currentTime || 0, ps = [];
    ['v', 'a'].forEach(function(k){
      ps.push(segRun(k, undefined, function(sb){
        var r = segRange(sb);
        if (!r || r.s >= ct - keep) return false;
        sb.remove(r.s, ct - keep);
      }).catch(function(){}));
    });
    return Promise.all(ps);
  }
  function segClear(kind){
    var sb = SG.sb[kind];
    try { sb.abort(); } catch (e) {}
    return segRun(kind, undefined, function(sb2){
      try { sb2.abort(); } catch (e) {}
      var r = segRange(sb2);
      if (!r) return false;
      sb2.remove(r.s, r.e + 1);
    }).catch(function(){});
  }

  // ---- adaptive bitrate on aggregate throughput (bytes over the union of busy time of the last 3 video segments) ----
  function segPx(e){ return (e.width > 0 ? e.width : e.height * 16 / 9) * e.height * (e.fps > 0 ? e.fps : 30); }
  // estimated Mbit/s needed to keep up with rung i (last observed segment size, else scaled from a known rung by pixels x fps)
  function segEstRate(i){
    if (SG.rate[i] > 0) return SG.rate[i];
    var best = -1, j, d;
    for (j = 0; j < SG.ladder.length; j++) if (SG.rate[j] > 0 && (best < 0 || Math.abs(j - i) < Math.abs(best - i))) best = j;
    if (best < 0) return 0;
    d = segPx(SG.ladder[i]) / segPx(SG.ladder[best]);
    return SG.rate[best] * d;
  }
  function segThroughput(){
    var s = SG.samp;
    if (s.length < 2) return null;
    var iv = s.map(function(x){ return [x.s, x.e]; }).sort(function(a, b){ return a[0] - b[0]; });
    var busy = 0, cs = iv[0][0], ce = iv[0][1], bytes = 0, i;
    for (i = 1; i < iv.length; i++) {
      if (iv[i][0] <= ce) ce = Math.max(ce, iv[i][1]);
      else { busy += ce - cs; cs = iv[i][0]; ce = iv[i][1]; }
    }
    busy += ce - cs;
    for (i = 0; i < s.length; i++) bytes += s[i].b;
    if (busy < 50) busy = 50;
    return bytes * 8 / 1000 / busy;   // bits / ms / 1000 = Mbit/s
  }
  function segAhead(){
    var r = segRange(SG.sb.v), a = segRange(SG.sb.a);
    if (!r || !a) return 0;
    return Math.max(0, Math.min(r.e, a.e) - (video.currentTime || 0));
  }
  function segStepDown(thr){
    var now = performance.now(), n = SG.vi - 1, i;
    if (SG.vi <= 0) return;
    if (thr > 0) {   // jump further down when one rung clearly is not enough
      for (i = SG.vi - 1; i >= 0; i--) { var rr = segEstRate(i); if (rr > 0 && thr >= 1.15 * rr) { n = i; break; } if (i === 0) n = 0; }
      if (n > SG.vi - 1) n = SG.vi - 1;
    }
    SG.vi = Math.max(0, n); SG.down++; SG.low = 0; SG.lastDown = now; SG.lastStep = now; SG.samp = [];
  }
  function segAbr(fromDone){
    if (!S.auto || !SG.started) return;
    var now = performance.now(), thr = segThroughput(), ahead = segAhead(), cap = segCapIdx();
    if (SG.vi > cap) { SG.vi = cap; return; }
    if (fromDone && thr !== null) {
      var req = segEstRate(SG.vi);
      if (req > 0 && thr < 1.15 * req) SG.low++; else SG.low = 0;
      if (SG.low >= 2) { segStepDown(thr); return; }
    }
    if (!video.paused && now - SG.startedAt > 8000 && ahead < SG.seg && now - SG.lastDown > 6000 && SG.vi > 0) { segStepDown(thr === null ? 0 : thr); return; }
    if (thr !== null && SG.vi < cap && now - SG.lastStep > 15000 && ahead >= 2 * SG.seg) {
      var nr = segEstRate(SG.vi + 1);
      if (nr > 0 && thr > 1.6 * nr) { SG.vi++; SG.up++; SG.low = 0; SG.lastStep = now; SG.samp = []; }
    }
  }
  function segNoteDone(e, res, t0){
    var idx = SG.ladder.indexOf(e), b = res.buf.byteLength;
    if (idx >= 0 && b > 0) SG.rate[idx] = b * 8 / SG.seg / 1e6;
    SG.samp.push({ s: t0, e: res.t1, b: b });
    if (SG.samp.length > 3) SG.samp.shift();
    segAbr(true);
  }

  // append buffered results strictly in sq order
  function segDrain(kind, gen){
    if (gen !== SG.gen || SG.draining[kind]) return;
    var sq = SG.nx[kind], it = SG.pend[kind][sq];
    if (!it) return;
    SG.draining[kind] = true;
    segAppend(kind, it.e, it.buf, gen).then(function(){
      if (gen !== SG.gen) return;
      delete SG.pend[kind][sq];
      SG.nx[kind] = sq + 1;
      SG.draining[kind] = false;
      segDrain(kind, gen);
      segPump('v', gen); segPump('a', gen);
    }).catch(function(){
      if (gen !== SG.gen) return;
      SG.draining[kind] = false;
      if (++SG.errs[kind] > 8) { S.err = 'Live stream connection lost.'; return; }
      setTimeout(function(){ segDrain(kind, gen); }, 1000);
    });
  }
  function segLaunch(kind, sq, gen){
    var isV = kind === 'v', e = isV ? SG.ladder[SG.vi] : SG.aud, t0 = performance.now(), live = true;
    SG.inf[kind]++;
    function fin(){ if (live) { live = false; SG.inf[kind]--; } }
    function retry(ms){
      SG.retry[kind].push(sq); SG.retry[kind].sort(function(a, b){ return a - b; });
      SG.hold[kind] = performance.now() + ms;
    }
    segFetch(e.token, sq, gen, false).then(function(res){
      if (gen !== SG.gen) return;
      fin();
      if (res.status === 200) {
        SG.errs[kind] = 0;
        if (isV) segNoteDone(e, res, t0);
        SG.pend[kind][sq] = { e: e, buf: res.buf };
        segDrain(kind, gen);
        segPump(kind, gen);
        return;
      }
      if (res.status === 404) {
        if (SG.head > 0 && sq < SG.head - 1) { setTimeout(function(){ if (gen === SG.gen) segGoLive(); }, 0); return; }   // fell out of the DVR window
        retry(SG.seg * 500);
        return;
      }
      throw new Error('http ' + res.status);
    }).catch(function(err){
      if (gen !== SG.gen) return;
      fin();
      if (++SG.errs[kind] > 8) { S.err = 'Live stream connection lost.'; return; }
      retry(1000 * Math.min(SG.errs[kind], 4));
    });
  }
  // start as many requests as allowed: video up to 3 in flight, audio 2
  function segPump(kind, gen){
    var isV = kind === 'v', other = isV ? 'a' : 'v', max = isV ? 3 : 2;
    while (gen === SG.gen && SG.inf[kind] < max) {
      if (performance.now() < SG.hold[kind]) return;
      var rt = SG.retry[kind].length > 0, sq = rt ? SG.retry[kind][0] : SG.rq[kind];
      if (!rt && SG.head > 0 && sq > SG.head + 1) return;           // not published yet
      if (sq - SG.nx[other] > 4) return;                             // keep audio and video within ~3 segments
      if (SG.started) {
        var r = segRange(SG.sb[kind]);
        if (r && r.e - (video.currentTime || 0) > 50) return;        // 50 s ahead of the playhead is enough
      }
      if (rt) SG.retry[kind].shift(); else SG.rq[kind] = sq + 1;
      segLaunch(kind, sq, gen);
    }
  }
  function segLoop(kind, gen){
    function tick(){
      if (gen !== SG.gen) return;
      segPump(kind, gen);
      segDrain(kind, gen);
      setTimeout(tick, 120);
    }
    tick();
  }

  // abort + empty both SourceBuffers, then restart both loops at sq
  function segRestart(sq){
    var gen = ++SG.gen;
    for (var i = 0; i < SG.ctl.length; i++) { try { SG.ctl[i].abort(); } catch (e) {} }
    SG.ctl = [];
    SG.nx = { v: sq, a: sq }; SG.rq = { v: sq, a: sq }; SG.inf = { v: 0, a: 0 };
    SG.pend = { v: {}, a: {} }; SG.retry = { v: [], a: [] }; SG.hold = { v: 0, a: 0 }; SG.errs = { v: 0, a: 0 }; SG.draining = { v: false, a: false };
    SG.started = false; SG.low = 0; SG.samp = [];
    S.buffering = true;
    return Promise.all([segClear('v'), segClear('a')]).then(function(){
      if (gen === SG.gen) { segLoop('v', gen); segLoop('a', gen); }
    });
  }
  // find the first sq >= target that exists (a 404 older than the head means outside the DVR window: step 60 forward)
  function segResolve(target, tries){
    return segFetch(SG.ladder[SG.vi].token, target, SG.gen, true).then(function(r){
      if (r.status === 200 || tries <= 0 || SG.head <= 0 || target >= SG.head - 2) return target;
      return segResolve(Math.min(target + 60, SG.head - 2), tries - 1);
    }).catch(function(){ return target; });
  }
  function segSeekSq(target){
    if (SG.head <= 0) return Promise.resolve();
    target = Math.floor(Math.max(SG.head - 720, Math.min(target, SG.head - 1)));
    var seq = ++SG.seekSeq;
    return segResolve(target, 14).then(function(sq){ if (seq === SG.seekSeq) return segRestart(sq); });
  }
  function segGoLive(){
    SG.seekSeq++;
    SG.rewound = false; SG.lastJump = performance.now();
    segFetch(SG.ladder[SG.vi].token, 1, SG.gen, true).catch(function(){}).then(function(){   // refreshes the head
      return segSeekSq(SG.head - 2);
    });
  }
  function segSeekTo(ts){
    if (!SG || !SG.started || SG.head <= 0) return;
    if (ts < (video.currentTime || 0) - 0.5) SG.rewound = true;
    else if (ts >= segLiveT() - 2 * SG.seg) SG.rewound = false;
    var r = segRange(SG.sb.v), a = segRange(SG.sb.a);
    if (r && a) {
      var lo = Math.max(r.s, a.s) + 0.3, hi = Math.min(r.e, a.e) - 1;
      if (ts >= lo && ts <= hi) { video.currentTime = ts; return; }
    }
    segSeekSq(segCurSq() + (ts - (video.currentTime || 0)) / SG.seg);
  }
  function segQuality(h){
    if (!SG) return;
    if (h === 'auto' || !h) { S.auto = true; SG.low = 0; if (SG.vi > segCapIdx()) SG.vi = segCapIdx(); return; }
    h = parseInt(h, 10);
    for (var i = 0; i < SG.ladder.length; i++) if (SG.ladder[i].height === h) {
      var was = SG.vi;
      S.auto = false; SG.vi = i;
      // a picked quality shows now, not after the ~15 s already buffered at the old one: refetch from the current segment
      if (i !== was && SG.started && SG.head > 0) segSeekSq(Math.floor(segCurSq()));
      return;
    }
  }
  function segState(){
    var t = video.currentTime || 0, hs = [], cur = SG && SG.ladder.length ? SG.ladder[SG.vi] : null;
    if (SG) for (var i = SG.ladder.length - 1; i >= 0; i--) hs.push(SG.ladder[i].height);
    var lt = SG ? segLiveT() : 0;
    var stalled = !SG || !SG.started || (!video.paused && video.readyState < 3);
    return JSON.stringify({
      t: t, d: Math.max(lt, t), ws: SG ? segWinStart() : 0, live: true, behind: Math.max(0, lt - t),
      paused: video.paused, buffering: stalled,
      ended: false, quality: S.auto ? 'auto' : (cur ? cur.height : 'auto'), height: cur ? cur.height : 0, qualities: hs,
      captions: [], caption: '', audios: [], audio: '', rate: S.rate, error: S.err, skipped: 0, ready: S.ready,
      dbg: SG ? segDbg(cur, lt - t) : ''
    });
  }
  function segDbg(cur, behind){
    if (!cur) return '';
    var thr = segThroughput();
    return 'r=' + cur.height + 'p' + (cur.fps > 0 ? Math.round(cur.fps) : '') + ' pic=' + video.videoWidth + 'x' + video.videoHeight + ' thr=' + (thr === null ? '-' : thr.toFixed(1)) + 'Mbps inf=' + (SG.inf.v + SG.inf.a)
      + ' ahead=' + segAhead().toFixed(1) + 's head=' + SG.head + ' cur=' + Math.round(segCurSq()) + ' behind=' + Math.max(0, behind).toFixed(1)
      + 's dn=' + SG.down + ' up=' + SG.up;
  }
  function segTick(){
    if (!SG || !SG.opened) return;
    var ct = video.currentTime || 0, r = segRange(SG.sb.v), a = segRange(SG.sb.a), now = performance.now();
    if (!SG.started) {
      if (r && a) {
        var s0 = Math.max(r.s, a.s);
        if (Math.min(r.e, a.e) > s0 + 0.5) {
          SG.started = true; S.ready = true; SG.startedAt = now; SG.lastStep = now;
          video.currentTime = s0;
          var p = video.play();
          if (p && p.catch) p.catch(function(){});
        }
      }
      return;
    }
    // jump small holes and a playhead that fell behind the buffered start
    if (!video.paused && video.readyState < 3 && r && a) {
      var s1 = Math.max(r.s, a.s);
      if (ct < s1 - 0.05 && Math.min(r.e, a.e) > s1 + 1) video.currentTime = s1 + 0.05;
      else {
        var vb = video.buffered;
        for (var i = 0; i < vb.length; i++) if (vb.start(i) > ct && vb.start(i) - ct < 1.5) { video.currentTime = vb.start(i) + 0.02; break; }
      }
    }
    segAbr(false);
    // stay near live: jump forward when far behind, nudge the speed up when slightly behind (never after the user rewound)
    var behind = segLiveT() - ct;
    if (!SG.rewound && !video.paused) {
      if (behind > 4 * SG.seg && now - SG.lastJump > 30000) { SG.lastJump = now; segGoLive(); }
      else if (behind > 2.5 * SG.seg) SG.catching = true;
    }
    if (SG.catching && (SG.rewound || behind < 2 * SG.seg)) SG.catching = false;
    var want = SG.catching ? S.rate * 1.05 : S.rate;
    if (Math.abs(video.playbackRate - want) > 0.001) video.playbackRate = want;
    if (now - SG.lastTrim > 5000) { SG.lastTrim = now; segTrim(90); }
    if (now - SG.headAt > SG.seg * 1500 && now - SG.lastProbe > 3000) {   // keeps behind accurate while paused / buffer full
      SG.lastProbe = now;
      segFetch(SG.ladder[SG.vi].token, SG.head + 1, SG.gen, true).catch(function(){});
    }
  }
  function initSeg(){
    if (CFG.error) return;
    var C = CFG.seg;
    if (!window.MediaSource) { S.err = 'This browser cannot play live streams.'; return; }
    var ladder = segLadder(C.video || []);
    if (!ladder.length || !MediaSource.isTypeSupported(C.audio.mime)) { S.err = 'This browser cannot play this live stream.'; return; }
    var ms = new MediaSource();
    SG = { ms: ms, seg: C.seconds || 5, ladder: ladder, aud: C.audio, vi: 0, head: 0, headTime: 0, headAt: performance.now(), gen: 0, seekSeq: 0,
      nx: { v: 0, a: 0 }, rq: { v: 0, a: 0 }, inf: { v: 0, a: 0 }, pend: { v: {}, a: {} }, retry: { v: [], a: [] }, hold: { v: 0, a: 0 }, errs: { v: 0, a: 0 },
      draining: { v: false, a: false }, samp: [], rate: {}, low: 0, down: 0, up: 0, lastDown: 0, lastStep: 0, lastJump: -1e9, startedAt: 0, rewound: false, catching: false,
      started: false, opened: false, vMime: '', sb: { v: null, a: null }, chain: { v: Promise.resolve(), a: Promise.resolve() },
      ctl: [], lastTrim: 0, lastProbe: 0 };
    SG.vi = segCapIdx();
    video.src = URL.createObjectURL(ms);
    ms.addEventListener('sourceopen', function(){
      if (SG.opened) return;
      SG.opened = true;
      try {
        SG.vMime = ladder[SG.vi].mime;
        SG.sb.v = ms.addSourceBuffer(SG.vMime);
        SG.sb.a = ms.addSourceBuffer(C.audio.mime);
      } catch (e) { S.err = 'Could not start the live player (' + (e && e.name ? e.name : 'error') + ').'; return; }
      segFetch(ladder[SG.vi].token, 1, 0, true).catch(function(){}).then(function(){   // sq=1 is a 404 that carries the head number
        if (SG.head <= 0) return segFetch(ladder[SG.vi].token, null, 0, true).catch(function(){});
      }).then(function(){
        if (SG.head <= 0) { S.err = 'This live stream is not available right now.'; return; }
        return segRestart(Math.max(1, SG.head - 2));
      });
    });
    video.addEventListener('error', function(){
      var er = video.error;
      if (er && !S.err) { S.err = 'Could not keep playing this live stream.'; S.dbg = 'video error ' + er.code; }
    });
    setInterval(segTick, 250);
  }
";
    }
}
