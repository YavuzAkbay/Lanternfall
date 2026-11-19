// Fullscreen for the Edge (WebView2) page, done the TV-browser way: WebView2 refuses element fullscreen inside an
// embedded composition control ("not granted"), so requestFullscreen is replaced in every frame by a stretch of the
// element over the whole viewport. A cross-origin player iframe asks its parent to stretch the <iframe> itself, up to
// the top document, which tells the host ("nomad-fs:1/0") so it can hide the top bar and hints.
namespace SeleneBrowser
{
    internal static class EdgeFullscreen
    {
        public const string Message = "nomad-fs:";

        // injected with AddScriptToExecuteOnDocumentCreatedAsync (runs in the top document and every child frame first)
        public const string Script = @"(function(){
if (window.__nomadFs) return; window.__nomadFs = true;
var isTop = window.top === window, fsEl = null, styleTag = null;
function css(){
  if (styleTag) return;
  styleTag = document.createElement('style');
  styleTag.textContent = '[data-nomad-fs]{position:fixed!important;left:0!important;top:0!important;right:auto!important;bottom:auto!important;' +
    'width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;min-width:0!important;min-height:0!important;' +
    'margin:0!important;padding:0!important;border:0!important;z-index:2147483647!important;background:#000!important;transform:none!important}' +
    'video[data-nomad-fs]{object-fit:contain!important}' +
    'html[data-nomad-fs-root],html[data-nomad-fs-root] body{overflow:hidden!important}';
  (document.head || document.documentElement).appendChild(styleTag);
}
function tell(on){
  if (isTop) { try { window.chrome.webview.postMessage('" + Message + @"' + (on ? 1 : 0)); } catch (e) {} }
  else { try { parent.postMessage({ nomadFs: on ? 1 : 0 }, '*'); } catch (e) {} }
}
function fire(el){
  try { (el || document).dispatchEvent(new Event('fullscreenchange', { bubbles: true })); } catch (e) {}
  try { (el || document).dispatchEvent(new Event('webkitfullscreenchange', { bubbles: true })); } catch (e) {}
}
function enter(el){
  if (!el || fsEl === el) return Promise.resolve();
  if (fsEl) leave(true);
  css(); fsEl = el;
  el.setAttribute('data-nomad-fs', ''); document.documentElement.setAttribute('data-nomad-fs-root', '');
  tell(true); fire(el);
  return Promise.resolve();
}
function leave(quiet){
  var el = fsEl; if (!el) return Promise.resolve();
  fsEl = null;
  el.removeAttribute('data-nomad-fs'); document.documentElement.removeAttribute('data-nomad-fs-root');
  if (!quiet) { tell(false); fire(el); }
  return Promise.resolve();
}
function frames(){ return document.getElementsByTagName('iframe'); }
function exitAll(){
  leave(false);
  var f = frames();
  for (var i = 0; i < f.length; i++) { try { f[i].contentWindow.postMessage({ nomadFsExit: 1 }, '*'); } catch (e) {} }
}
var request = function(){ return enter(this); };
Element.prototype.requestFullscreen = request;
Element.prototype.webkitRequestFullscreen = request;
Element.prototype.webkitRequestFullScreen = request;
if (window.HTMLVideoElement) HTMLVideoElement.prototype.webkitEnterFullscreen = request;
var exit = function(){ return leave(false); };
document.exitFullscreen = exit; document.webkitExitFullscreen = exit; document.webkitCancelFullScreen = exit;
function getter(name, fn){ try { Object.defineProperty(Document.prototype, name, { configurable: true, get: fn }); } catch (e) {} }
getter('fullscreenElement', function(){ return fsEl; });
getter('webkitFullscreenElement', function(){ return fsEl; });
getter('webkitCurrentFullScreenElement', function(){ return fsEl; });
getter('fullscreen', function(){ return !!fsEl; });
getter('webkitIsFullScreen', function(){ return !!fsEl; });
getter('fullscreenEnabled', function(){ return true; });
getter('webkitFullscreenEnabled', function(){ return true; });
window.addEventListener('message', function(e){
  var d = e.data; if (!d || typeof d !== 'object') return;
  if ('nomadFs' in d) {   // a child frame's player went fullscreen / left it: stretch that frame here
    var f = frames(), frame = null;
    for (var i = 0; i < f.length; i++) if (f[i].contentWindow === e.source) { frame = f[i]; break; }
    if (!frame) return;
    if (d.nomadFs) enter(frame); else if (fsEl === frame) leave(false);
  } else if (d.nomadFsExit) exitAll();   // the host asked to leave: pass it down
});
document.addEventListener('keydown', function(e){ if (e.key === 'Escape' && fsEl) exitAll(); }, true);
window.__nomadExitFs = exitAll;
})();";
    }
}
