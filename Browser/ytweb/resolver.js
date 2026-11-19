// Lanternfall YouTube web route. Runs in a hidden Edge WebView2 page that YouTubeWeb.cs serves as
// https://www.youtube.com/__lanternfall/resolver.html, so YouTube's own endpoints are same-origin (no CORS, no proxy).
// It is used when YouTube refuses the plain app client ("Sign in to confirm you're not a bot") and does what FreeTube does:
//   1. a YouTube.js session (visitor data, the current player script),
//   2. BotGuard, YouTube's anti-bot VM: its snapshot buys an integrity token, which yields a minter of Proof-of-Origin
//      tokens (a minimal port of LuanRT/BgUtils, MIT),
//   3. the TV client's player answer, asked for with a token bound to the video; its stream urls are deciphered with the
//      player script and carry a token bound to the visitor (pot=), without which googlevideo stops after ~1 MB.
// The answer goes back to C# as a player-response-shaped JSON through chrome.webview.postMessage.
import { Innertube, Platform } from './youtubei.js';

// YouTube.js builds the decipher code from the player script; evaluating it is left to the host
Platform.shim.eval = async (data) => new Function(data.output)();

const REQUEST_KEY = 'O43z0dpjhgX20SCx4KAo';                  // YouTube's BotGuard request key
const GOOG_API_KEY = 'AIzaSyDyT5W0Jh49F30Pqqtyfdf7pDLFKLJoAnw';
const CLIENT = 'TV_SIMPLY';                                  // gives plain (cipher) urls; WEB/MWEB are SABR-only now
const fetchOmit = (input, init) => fetch(input, Object.assign({}, init || {}, { credentials: 'omit' }));

let yt = null, ytAt = 0;
let minter = null, minterUntil = 0;

function post(m) { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (e) { } }

function withTimeout(p, ms, what) {
  return Promise.race([p, new Promise((_, rej) => setTimeout(() => rej(new Error(what)), ms))]);
}

function b64ToU8(s) {
  s = s.replace(/-/g, '+').replace(/_/g, '/').replace(/\./g, '=');
  const b = atob(s);
  const u = new Uint8Array(b.length);
  for (let i = 0; i < b.length; i++) u[i] = b.charCodeAt(i);
  return u;
}

function u8ToB64(u) {
  let s = '';
  for (let i = 0; i < u.length; i++) s += String.fromCharCode(u[i]);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_');
}

function loadScript(url) {
  return new Promise((res, rej) => {
    const s = document.createElement('script');
    s.src = url;
    s.onload = res;
    s.onerror = () => rej(new Error('BotGuard script did not load'));
    (document.head || document.documentElement).appendChild(s);
  });
}

async function session() {
  if (!yt || Date.now() - ytAt > 3 * 3600e3) {
    yt = await Innertube.create({ retrieve_player: true, fetch: fetchOmit });
    ytAt = Date.now();
    minter = null;   // tokens are bound to this session's visitor
  }
  return yt;
}

async function getMinter(y) {
  if (minter && Date.now() < minterUntil) return minter;
  const ch = await y.getAttestationChallenge('ENGAGEMENT_TYPE_UNBOUND');
  const bg = ch && ch.bg_challenge;
  if (!bg || !bg.program || !bg.global_name) throw new Error('no BotGuard challenge');
  if (!window[bg.global_name]) {
    let url = bg.interpreter_url && bg.interpreter_url.private_do_not_access_or_else_trusted_resource_url_wrapped_value;
    if (!url) throw new Error('no BotGuard script');
    if (url.startsWith('//')) url = 'https:' + url;
    await loadScript(url);
  }
  const vm = window[bg.global_name];
  if (!vm || typeof vm.a !== 'function') throw new Error('BotGuard unavailable');
  let gotFns;
  const fns = new Promise(r => { gotFns = r; });
  const noop = () => { };
  await vm.a(bg.program, (asyncSnapshot) => gotFns(asyncSnapshot), true, undefined, noop, [[], []], undefined, false, [noop, noop, noop, noop, noop]);
  const snapshot = await withTimeout(fns, 5000, 'BotGuard did not start');
  const signals = [];
  const bgResponse = await withTimeout(new Promise(res => snapshot(res, [undefined, undefined, signals, undefined])), 5000, 'BotGuard snapshot timed out');
  const r = await fetch('/api/jnn/v1/GenerateIT', {
    method: 'POST', credentials: 'omit',
    headers: { 'content-type': 'application/json+protobuf', 'x-goog-api-key': GOOG_API_KEY, 'x-user-agent': 'grpc-web-javascript/0.1' },
    body: JSON.stringify([REQUEST_KEY, bgResponse])
  });
  if (!r.ok) throw new Error('integrity token: HTTP ' + r.status);
  const it = await r.json();
  if (!it || typeof it[0] !== 'string') throw new Error('no integrity token');
  if (typeof signals[0] !== 'function') throw new Error('no token minter');
  const mint = await signals[0](b64ToU8(it[0]));
  if (typeof mint !== 'function') throw new Error('token minter failed');
  const ttl = typeof it[1] === 'number' ? it[1] : 3600;
  minterUntil = Date.now() + Math.max(60, ttl - 600) * 1000;
  minter = async (binding) => {
    const out = await mint(new TextEncoder().encode(binding));
    if (!(out instanceof Uint8Array)) throw new Error('token mint failed');
    return u8ToB64(out);
  };
  return minter;
}

function withPot(url, pot) {
  try {
    const u = new URL(url);
    u.searchParams.set('potc', '1');
    u.searchParams.set('pot', pot);
    u.searchParams.set('c', 'TVHTML5_SIMPLY');
    return u.toString();
  } catch (e) { return url; }
}

async function resolve(videoId) {
  const y = await session();
  const mint = await getMinter(y);
  const visitor = decodeURIComponent(y.session.context.client.visitorData || '');
  const potVideo = await mint(videoId);
  const potVisitor = await mint(visitor);
  const info = await y.getBasicInfo(videoId, { client: CLIENT, po_token: potVideo });
  const ps = info.playability_status || {};
  if (ps.status !== 'OK') return { playabilityStatus: { status: ps.status || 'ERROR', reason: ps.reason || '' } };
  y.session.player.po_token = potVisitor;   // YouTube.js appends it to every deciphered url as pot=
  const formats = [];
  const list = (info.streaming_data && info.streaming_data.adaptive_formats) || [];
  for (const f of list) {
    let url;
    try { url = await f.decipher(y.session.player); } catch (e) { continue; }
    if (!url || url.indexOf('sabr=1') >= 0) continue;
    formats.push({
      itag: f.itag, url: url, mimeType: f.mime_type, bitrate: f.bitrate, width: f.width, height: f.height, fps: f.fps,
      contentLength: f.content_length ? String(f.content_length) : '0',
      initRange: f.init_range ? { start: String(f.init_range.start), end: String(f.init_range.end) } : undefined,
      indexRange: f.index_range ? { start: String(f.index_range.start), end: String(f.index_range.end) } : undefined,
      qualityLabel: f.quality_label || '',
      audioTrack: f.audio_track ? { id: f.audio_track.id || '', displayName: f.audio_track.display_name || '', audioIsDefault: !!f.audio_track.audio_is_default } : undefined,
      isDrc: !!f.is_drc
    });
  }
  const b = info.basic_info || {};
  const tracks = (info.captions && info.captions.caption_tracks) || [];
  const captions = tracks.map(c => ({
    baseUrl: withPot(c.base_url, potVisitor), languageCode: c.language_code || '',
    name: { simpleText: (c.name && c.name.text) || c.language_code || '' }, kind: c.kind || ''
  }));
  return {
    playabilityStatus: { status: 'OK' },
    videoDetails: {
      videoId: videoId, title: b.title || '', author: b.author || '', channelId: b.channel_id || '',
      lengthSeconds: String(b.duration || 0), isLive: !!b.is_live, isLiveContent: !!b.is_live_content
    },
    streamingData: { adaptiveFormats: formats },
    captions: { playerCaptionsTracklistRenderer: { captionTracks: captions } }
  };
}

window.lfResolve = (id, videoId) => {
  resolve(videoId).then(
    json => post({ id: id, ok: true, json: JSON.stringify(json) }),
    e => {
      yt = null; minter = null;   // start clean next time (a stale session or VM is the usual cause)
      post({ id: id, ok: false, error: String((e && e.message) || e) });
    });
};

post({ ready: true });
