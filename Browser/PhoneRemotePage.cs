// PhoneRemotePage.cs - the ONE page the phone opens (PhoneRemote.cs serves it). It follows what is on the TV: the keyboard when one is
// open, YouTube controls and search in YouTube, browser controls in the browser, a "send a link" box everywhere. One self-contained
// HTML string; the only outside requests are YouTube thumbnails (img-src). Single quotes only inside, so it can sit in a C#
// verbatim string. C# 5 only.
namespace SeleneBrowser
{
    internal static class PhoneRemotePage
    {
        public const string Csp = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; img-src https://i.ytimg.com https://*.ytimg.com data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        public const string Html = @"<!doctype html>
<html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,viewport-fit=cover'>
<meta name='robots' content='noindex,nofollow'>
<meta name='mobile-web-app-capable' content='yes'><meta name='apple-mobile-web-app-capable' content='yes'>
<meta name='theme-color' content='#12151b'>
<title>TV remote</title>
<style>
:root{--a:#7aa2ff}
*{box-sizing:border-box}
html,body{margin:0;min-height:100%;background:#12151b;color:#f2f4f8;font:17px/1.4 system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}
body{padding:16px 16px calc(24px + env(safe-area-inset-bottom));max-width:640px;margin:0 auto}
.k{margin:0;font-size:13px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:#8f98a6}
h1{margin:2px 0 4px;font-size:26px;font-weight:650;word-break:break-word}
#ctx{font-size:14px;color:#8f98a6;margin:0 0 14px}
.row{display:flex;gap:10px}
input[type=text]{flex:1;min-width:0;height:54px;border-radius:27px;border:2px solid #2a3140;background:#1b2029;color:inherit;font:inherit;font-size:18px;padding:0 20px;-webkit-appearance:none}
input[type=text]:focus,#kx:focus{outline:none;border-color:var(--a)}
button{border:0;font:inherit;color:#f2f4f8;background:#2a3140}
button:active{opacity:.8}
button:disabled{opacity:.45}
#go{width:96px;height:54px;border-radius:27px;font-weight:650;font-size:18px;background:var(--a);color:#0b0e14}
.chips{display:flex;gap:8px;margin:10px 2px 0}
.chips button{height:34px;padding:0 16px;border-radius:17px;font-size:14px;background:#1b2029;border:1px solid #2a3140;color:#aab1bd}
.chips button.on{background:#2a3140;color:#f2f4f8;border-color:var(--a)}
#st{min-height:22px;margin:10px 4px;font-size:14px;color:#8f98a6}
#st.bad{color:#ff9d9d}
.card{margin:6px 0 16px;padding:14px 16px;border-radius:20px;background:#1b2029;border:1px solid #2a3140}
#kb{display:none}
#kt{margin:0 0 10px;font-size:22px;font-weight:650;word-break:break-word}
#kx{width:100%;min-height:92px;resize:none;border-radius:18px;border:2px solid #2a3140;background:#12151b;color:inherit;font:inherit;font-size:21px;padding:14px 16px;-webkit-appearance:none}
#kx.pw{-webkit-text-security:disc}
#sw{display:none;font-size:15px;color:#aab1bd;margin:10px 0 0}
#sw input{margin-right:8px;width:18px;height:18px;vertical-align:-3px}
.big{flex:1;height:54px;border-radius:27px;font-weight:650;font-size:18px}
#kok{background:var(--a);color:#0b0e14;flex:2}
#yt,#br{display:none}
#nt{font-size:19px;font-weight:650;word-break:break-word}
#na{font-size:14px;color:#8f98a6;margin-top:2px}
#bar{height:5px;border-radius:3px;background:#2a3140;margin:12px 0 4px;overflow:hidden}
#bar i{display:block;height:100%;width:0;background:var(--a)}
#tm{font-size:13px;color:#8f98a6;display:flex;justify-content:space-between}
.ctl{display:flex;gap:8px;margin-top:12px}
.ctl button{flex:1;height:52px;border-radius:26px;font-size:18px}
.ctl #pp{flex:1.6;background:var(--a);color:#0b0e14}
#bt{font-size:17px;font-weight:650;word-break:break-word}
#bu{font-size:13px;color:#8f98a6;word-break:break-all;margin-top:2px}
#pad{margin-top:12px;height:230px;border-radius:22px;background:#12151b;border:2px dashed #2a3140;display:flex;align-items:center;justify-content:center;text-align:center;padding:0 24px;color:#6f7886;font-size:14px;touch-action:none;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none}
#pad.on{border-color:var(--a);color:#aab1bd}
#pclick{margin-top:8px;width:100%;height:54px;border-radius:27px;font-size:17px;font-weight:650}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-top:12px}
.grid button{height:54px;border-radius:27px;font-size:17px}
.grid .wide{grid-column:1/3}
#res{list-style:none;margin:0;padding:0}
#res li{display:flex;gap:12px;align-items:center;padding:8px;margin:0 -8px;border-radius:14px}
#res li:active{background:#1b2029}
#res img{width:120px;height:68px;border-radius:10px;object-fit:cover;background:#1b2029;flex:none}
#res b{display:block;font-size:16px;line-height:1.25;font-weight:600;word-break:break-word}
#res span{display:block;font-size:13px;color:#8f98a6;margin-top:2px}
.tip{margin-top:22px;font-size:13px;color:#6f7886}
</style></head><body>
<p class='k'>Lanternfall</p>
<h1 id='h'>TV remote</h1>
<p id='ctx'>Connecting...</p>

<div id='kb' class='card'>
 <p class='k'>Typing on the TV</p>
 <h2 id='kt'></h2>
 <textarea id='kx' autocomplete='off' autocorrect='off' spellcheck='false' autocapitalize='none' enterkeyhint='done'></textarea>
 <label id='sw'><input type='checkbox' id='show'>Show what I type</label>
 <div class='row' style='margin-top:12px'><button id='kno' class='big' type='button'>Cancel</button><button id='kok' class='big' type='button'>Done</button></div>
</div>

<div id='main'>
 <form id='f' class='row' autocomplete='off'><input id='q' type='text' inputmode='url' enterkeyhint='go' autocomplete='off' autocorrect='off' autocapitalize='none' spellcheck='false'><button id='go' type='submit'>Send</button></form>
 <div class='chips'><button id='cy' type='button'>Search YouTube</button><button id='cw' type='button'>Search the web</button></div>
 <div id='st'></div>

 <div id='yt' class='card'>
  <div id='now'>
   <div class='k'>Playing on the TV</div><div id='nt'></div><div id='na'></div>
   <div id='bar'><i id='bi'></i></div><div id='tm'><span id='t0'></span><span id='t1'></span></div>
   <div class='ctl'><button id='pv' type='button'>&#9198;</button><button id='bk' type='button'>-10</button><button id='pp' type='button'>&#9199;</button><button id='fw' type='button'>+10</button><button id='nx' type='button'>&#9197;</button></div>
   <div class='ctl'><button id='sp' type='button'>Stop video</button></div>
  </div>
  <div id='nonow' class='k'>Nothing playing. Paste a YouTube link or search above.</div>
 </div>

 <div id='br' class='card'>
  <div class='k'>Browser on the TV</div><div id='bt'></div><div id='bu'></div>
  <div id='pad'>Trackpad: drag to move the cursor, tap to click, two fingers to scroll</div>
  <button id='pclick' type='button'>Click</button>
  <div class='grid'>
   <button id='b_back' type='button'>Back</button><button id='b_forward' type='button'>Forward</button>
   <button id='b_up' type='button'>Scroll up</button><button id='b_down' type='button'>Scroll down</button>
   <button id='b_zoomout' type='button'>Zoom -</button><button id='b_zoomin' type='button'>Zoom +</button>
   <button id='b_reload' type='button'>Reload</button><button id='b_home' type='button'>Start page</button>
   <button id='b_newtab' type='button'>New tab</button><button id='b_closetab' type='button'>Close tab</button>
  </div>
 </div>

 <ul id='res'></ul>
 <p class='tip'>Paste a YouTube link and it plays on the TV. Any other link opens in the TV's browser. When a keyboard opens on the TV, it appears here.</p>
</div>
<script>
(function(){
var base=location.pathname.replace(/\/+$/,'');
function $(i){return document.getElementById(i)}
var q=$('q'),st=$('st'),res=$('res'),kx=$('kx');
var lost=0,busy=false,ctx='',mode='yt',userMode=false;
var kbOn=false,kbKnown=-1,kbDirty=false,kbBusy=false,kbTimer=null,kbMulti=false,kbCfg='',kbEnded='';
function call(m,p,b,cb){var x=new XMLHttpRequest();x.open(m,base+p,true);x.timeout=20000;
 x.onload=function(){var j=null;try{j=JSON.parse(x.responseText)}catch(e){}cb(x.status,j)};
 x.onerror=x.ontimeout=function(){cb(0,null)};x.send(b===null?null:b)}
function say(s,bad){st.textContent=s;st.className=bad?'bad':''}
function fmt(s){s=Math.max(0,Math.floor(s||0));var h=Math.floor(s/3600),m=Math.floor(s%3600/60),r=s%60;
 return (h?h+':'+(m<10?'0':'')+m:m)+':'+(r<10?'0':'')+r}
function clear(){while(res.firstChild)res.removeChild(res.firstChild)}
function show(el,on){el.style.display=on?'block':'none'}

// ---------- layout follows the TV
function layout(){
 show($('kb'),kbOn);show($('main'),!kbOn);
 show($('yt'),ctx==='youtube');show($('br'),ctx==='browser');
 $('cy').className=mode==='yt'?'on':'';$('cw').className=mode==='web'?'on':'';
 q.placeholder=mode==='web'?'Address or web search':'Link or YouTube search';
 var names={youtube:'YouTube is open on the TV',browser:'The browser is open on the TV',home:'Send something to the TV'};
 $('ctx').textContent=kbOn?'A keyboard is open on the TV':(names[ctx]||'Connected to the TV')}
function setMode(m){mode=m;userMode=true;layout()}
$('cy').addEventListener('click',function(){setMode('yt')});
$('cw').addEventListener('click',function(){setMode('web')});

// ---------- send a link or words
function play(id,title){say('Starting on the TV...');
 call('POST','/play',JSON.stringify({id:id}),function(s,j){
  if(j&&j.ok){say('Sent to the TV: '+(title||'video'));clear();q.value=''}else say(j&&j.error?j.error:'Could not reach the TV.',true)})}
function listResults(list){clear();for(var i=0;i<list.length;i++)(function(v){
 var li=document.createElement('li'),im=document.createElement('img'),d=document.createElement('div'),b=document.createElement('b'),s=document.createElement('span');
 im.src=v.thumb||'';im.alt='';b.textContent=v.title||'';s.textContent=(v.author||'')+(v.duration?'  '+v.duration:'');
 d.appendChild(b);d.appendChild(s);li.appendChild(im);li.appendChild(d);
 li.addEventListener('click',function(){play(v.id,v.title)});res.appendChild(li)})(list[i])}
function ytsearch(words){say('Searching YouTube...');clear();
 call('GET','/search?q='+encodeURIComponent(words),null,function(s,j){
  if(j&&j.ok){if(!j.results.length)say('Nothing found for that.',true);else{say('Tap a video to play it on the TV.');listResults(j.results)}}
  else say(j&&j.error?j.error:'Could not reach the TV.',true)})}
function send(){var t=q.value.replace(/^\s+|\s+$/g,'');if(!t||busy)return;busy=true;say('Sending...');
 call('POST','/send',JSON.stringify({text:t,mode:mode}),function(s,j){busy=false;
  if(!j){say('Could not reach the TV. Is it on the same Wi-Fi?',true);return}
  if(!j.ok){say(j.error||'The TV could not do that.',true);return}
  if(j.kind==='search'){ytsearch(j.q);return}
  q.value='';clear();say(j.kind==='video'?'Playing on the TV.':(j.kind==='web'?'Searching on the TV.':'Opened in the TV browser.'))})}
$('f').addEventListener('submit',function(e){e.preventDefault();q.blur();send()});
q.addEventListener('paste',function(){setTimeout(function(){if(/^\s*(https?:\/\/|(www\.)?(youtu\.be|youtube\.com))\S*\s*$/i.test(q.value))send()},60)});

// ---------- YouTube controls
function ctl(c){call('POST','/ctl',JSON.stringify({cmd:c}),function(s,j){if(!(j&&j.ok))say(j&&j.error?j.error:'Could not reach the TV.',true)})}
$('pp').addEventListener('click',function(){ctl('toggle')});
$('bk').addEventListener('click',function(){ctl('back')});
$('fw').addEventListener('click',function(){ctl('forward')});
$('nx').addEventListener('click',function(){ctl('next')});
$('pv').addEventListener('click',function(){ctl('previous')});
$('sp').addEventListener('click',function(){ctl('stop')});

// ---------- browser controls (scrolling repeats while held)
function bc(c){call('POST','/bctl',JSON.stringify({cmd:c}),function(s,j){if(!(j&&j.ok))say(j&&j.error?j.error:'Could not reach the TV.',true)})}
function hook(id,repeat){var b=$('b_'+id),t=null;
 function go(){bc(id)}
 function start(e){e.preventDefault();go();if(repeat)t=setInterval(go,240)}
 function stop(){if(t){clearInterval(t);t=null}}
 b.addEventListener('touchstart',start,{passive:false});b.addEventListener('touchend',stop);b.addEventListener('touchcancel',stop);
 b.addEventListener('mousedown',start);b.addEventListener('mouseup',stop);b.addEventListener('mouseleave',stop)}
hook('back',false);hook('forward',false);hook('reload',false);hook('home',false);hook('newtab',false);hook('closetab',false);
hook('up',true);hook('down',true);hook('zoomin',false);hook('zoomout',false);

// ---------- trackpad: moves the TV browser's cursor (the one the right stick moves)
(function(){
 var pad=$('pad'),mx=0,my=0,sy=0,busy=false,timer=null,one=null,t0=0,moved=0,multi=false,lastY=0,down=false;
 function send(a,dx,dy){busy=true;call('POST','/ptr',JSON.stringify({a:a,dx:dx,dy:dy}),function(s,j){busy=false;if(j&&!j.ok&&j.error)say(j.error,true)})}
 function pump(){if(busy)return;
  var dx=Math.round(mx),dy=Math.round(my);
  if(dx||dy){mx-=dx;my-=dy;send('move',dx,dy);return}
  var d=Math.round(sy);if(d){sy-=d;send('scroll',0,d)}}
 function run(){if(!timer)timer=setInterval(pump,35);pad.className='on'}
 function stop(){setTimeout(function(){if(!down&&timer){clearInterval(timer);timer=null}},250);pad.className=''}
 function click(){call('POST','/ptr',JSON.stringify({a:'click',dx:0,dy:0}),function(s,j){if(j&&!j.ok&&j.error)say(j.error,true)})}
 function pt(e){var t=e.touches&&e.touches.length?e.touches[0]:e;return{x:t.clientX,y:t.clientY}}
 function gain(dx,dy){return 2.3+Math.min(Math.sqrt(dx*dx+dy*dy)/5,3)}
 pad.addEventListener('touchstart',function(e){e.preventDefault();down=true;
  if(e.touches.length===1){one=pt(e);t0=Date.now();moved=0;multi=false}
  else{multi=true;lastY=(e.touches[0].clientY+e.touches[1].clientY)/2}
  run()},{passive:false});
 pad.addEventListener('touchmove',function(e){e.preventDefault();
  if(e.touches.length===1&&!multi){var p=pt(e),dx=p.x-one.x,dy=p.y-one.y;one=p;moved+=Math.abs(dx)+Math.abs(dy);var g=gain(dx,dy);mx+=dx*g;my+=dy*g}
  else if(e.touches.length>=2){var y=(e.touches[0].clientY+e.touches[1].clientY)/2;sy+=(y-lastY)*4;lastY=y;moved+=20}},{passive:false});
 pad.addEventListener('touchend',function(e){e.preventDefault();
  if(e.touches.length===0){down=false;if(!multi&&moved<10&&Date.now()-t0<300)click();multi=false;stop()}},{passive:false});
 pad.addEventListener('touchcancel',function(){down=false;multi=false;stop()});
 pad.addEventListener('mousedown',function(e){e.preventDefault();down=true;one=pt(e);t0=Date.now();moved=0;multi=false;run()});
 window.addEventListener('mousemove',function(e){if(!down||multi)return;var p=pt(e),dx=p.x-one.x,dy=p.y-one.y;one=p;moved+=Math.abs(dx)+Math.abs(dy);var g=gain(dx,dy);mx+=dx*g;my+=dy*g});
 window.addEventListener('mouseup',function(){if(!down)return;down=false;if(moved<10&&Date.now()-t0<300)click();stop()});
 $('pclick').addEventListener('click',click);
})();

// ---------- the TV's keyboard
function kbSetup(k){var cfg=k.title+'|'+k.multi+'|'+k.secret+'|'+k.accent;if(cfg===kbCfg)return;kbCfg=cfg;kbKnown=-1;kbDirty=false;
 $('kt').textContent=k.title||'Type here';kbMulti=!!k.multi;
 document.documentElement.style.setProperty('--a',k.accent||'#7aa2ff');
 kx.style.height=kbMulti?'38vh':'92px';kx.setAttribute('autocapitalize',kbMulti?'sentences':'none');
 $('sw').style.display=k.secret?'block':'none';$('show').checked=false;kx.className=k.secret?'pw':''}
function kbSend(cb){if(kbBusy)return;kbBusy=true;kbDirty=false;
 call('POST','/kb/text',JSON.stringify({text:kx.value}),function(s,j){kbBusy=false;
  if(s===410){kbGone();return}
  if(j&&j.v!==undefined)kbKnown=j.v;
  if(kbDirty)kbSend();
  if(cb)cb()})}
function kbFlush(cb){if(!kbDirty&&!kbBusy){cb();return}
 if(kbBusy){setTimeout(function(){kbFlush(cb)},60);return}
 kbSend(function(){kbFlush(cb)})}
function kbGone(){kbOn=false;kbCfg='';layout();if(kbEnded){say(kbEnded);kbEnded=''}else say('The keyboard closed on the TV.')}
function kbFinish(path,msg){kbFlush(function(){call('POST',path,'',function(s,j){
 if(j&&j.ok){kbEnded=msg;kbOn=false;kbCfg='';layout();say(msg);kbEnded=''}else if(s===410)kbGone();else say('Could not reach the TV. Try again.',true)})})}
kx.addEventListener('input',function(){kbDirty=true;clearTimeout(kbTimer);kbTimer=setTimeout(function(){kbSend()},120)});
kx.addEventListener('keydown',function(e){if(!kbMulti&&(e.key==='Enter'||e.keyCode===13)){e.preventDefault();$('kok').click()}});
$('kok').addEventListener('click',function(){kbFinish('/kb/done','Sent to the TV.')});
$('kno').addEventListener('click',function(){kbFinish('/kb/cancel','Cancelled.')});
$('show').addEventListener('change',function(){kx.className=$('show').checked?'':'pw'});

// ---------- follow the TV
function nowPlaying(j){
 var p=ctx==='youtube'&&!!j.playing;show($('now'),p);show($('nonow'),!p);
 if(!p)return;
 $('nt').textContent=j.title||'';$('na').textContent=j.author||'';
 var d=j.d||0,t=j.t||0;$('bi').style.width=(d>0?Math.min(100,t/d*100):0)+'%';
 $('t0').textContent=fmt(t);$('t1').textContent=j.live?'LIVE':(d>0?fmt(d):'');
 $('pp').innerHTML=j.paused?'&#9654;':'&#9208;'}
function poll(){call('GET','/state',null,function(s,j){
 if(j&&j.ok){
  if(lost>2||st.textContent==='')say('Connected to the TV.');
  lost=0;
  var k=j.kb;
  if(k){var first=!kbOn;kbOn=true;kbSetup(k);
   if(!kbDirty&&!kbBusy&&k.v!==kbKnown){kbKnown=k.v;if(kx.value!==k.text)kx.value=k.text}
   if(first){layout();setTimeout(function(){kx.focus()},60)}}
  else if(kbOn){kbGone()}
  if(j.ctx!==ctx){ctx=j.ctx;if(!userMode)mode=ctx==='browser'?'web':'yt';layout()}
  if(ctx==='browser'){$('bt').textContent=j.tab||'';$('bu').textContent=j.url||''}
  nowPlaying(j)}
 else if(++lost>2)say('Lost the TV. Is it still on the same Wi-Fi?',true);
 setTimeout(poll,k_interval())})}
function k_interval(){return kbOn?450:1200}
layout();poll();
})();
</script></body></html>";
    }
}
