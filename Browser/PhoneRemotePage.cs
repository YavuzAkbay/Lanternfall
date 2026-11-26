// PhoneRemotePage.cs - the ONE page the phone opens (PhoneRemote.cs serves it): a remote for the whole theme. Three tabs:
// Remote (a controller: round d-pad with swipe, A B X Y, View / Menu, shoulders; every press goes through the TV's own pad path, so
// every screen reacts as to the real pad; it adds the YouTube player in YouTube and a trackpad + tools in the browser), Games (the
// library with covers, search and filters; Play starts the game on the TV) and Send (a link or search for YouTube / the browser).
// When a keyboard opens on the TV it takes over the phone. The page wears the theme's accent colour. One self-contained HTML string;
// the only outside requests are YouTube thumbnails (img-src). Single quotes and ASCII only inside, so it can sit in a C# verbatim
// string. C# 5 only.
namespace SeleneBrowser
{
    internal static class PhoneRemotePage
    {
        public const string Csp = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; img-src 'self' https://i.ytimg.com https://*.ytimg.com data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        public const string Html = @"<!doctype html>
<html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover'>
<meta name='robots' content='noindex,nofollow'>
<meta name='mobile-web-app-capable' content='yes'><meta name='apple-mobile-web-app-capable' content='yes'>
<meta name='apple-mobile-web-app-status-bar-style' content='black-translucent'>
<meta name='theme-color' content='#0e1117'>
<title>Lanternfall remote</title>
<style>
:root{--a:#7aa2ff;--on:#0b0e14;--bg:#0e1117;--s1:#181d26;--s2:#212733;--ln:#2c3341;--ink:#f2f4f8;--mu:#949daa;--dim:#6b7483;--bar:64px}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent;touch-action:manipulation}
html{-webkit-text-size-adjust:100%;text-size-adjust:100%}
html,body{margin:0;height:100%;background:var(--bg);color:var(--ink);font:16px/1.4 system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;overscroll-behavior:none}
body{-webkit-user-select:none;user-select:none;-webkit-touch-callout:none}
input,textarea{-webkit-user-select:text;user-select:text}
button{border:0;font:inherit;color:inherit;background:none;padding:0;cursor:pointer;touch-action:manipulation}
button:disabled{opacity:.4}
svg{display:block}
.wrap{max-width:560px;margin:0 auto;padding:0 16px}

/* ---------- header */
header{position:sticky;top:0;z-index:5;background:linear-gradient(var(--bg) 70%,rgba(14,17,23,0));padding:calc(12px + env(safe-area-inset-top)) 0 10px}
.top{display:flex;align-items:center;gap:10px}
.brand{font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--mu);flex:1;display:flex;align-items:center;gap:8px}
.brand i{width:9px;height:9px;border-radius:50%;background:var(--a);box-shadow:0 0 10px var(--a)}
#conn{display:flex;align-items:center;gap:6px;font-size:12px;color:var(--mu);padding:5px 10px;border-radius:14px;background:var(--s1);border:1px solid var(--ln)}
#conn b{width:7px;height:7px;border-radius:50%;background:#e0b341}
#conn.cok b{background:#4fd08a}#conn.cbad b{background:#ff6b6b}
#now{margin:8px 0 0;font-size:21px;font-weight:650;line-height:1.2;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#now small{display:block;font-size:13px;font-weight:500;color:var(--mu);margin-bottom:2px;letter-spacing:.02em}

main{padding-bottom:calc(var(--bar) + 24px + env(safe-area-inset-bottom))}
.view{display:none}.view.on{display:block}

/* ---------- cards */
.card{background:var(--s1);border:1px solid var(--ln);border-radius:22px;padding:14px 16px;margin:6px 0 14px}
.k{font-size:12px;font-weight:700;letter-spacing:.07em;text-transform:uppercase;color:var(--mu);margin:0 0 6px}
.t1{font-size:17px;font-weight:650;line-height:1.25;word-break:break-word}
.t2{font-size:13px;color:var(--mu);margin-top:2px;word-break:break-word}
.note{font-size:14px;color:var(--mu)}

/* ---------- player */
#bar{height:5px;border-radius:3px;background:var(--ln);margin:10px 0 4px;overflow:hidden}
#bar i{display:block;height:100%;width:0;background:var(--a);transition:width .9s linear}
#tm{font-size:12px;color:var(--mu);display:flex;justify-content:space-between;font-variant-numeric:tabular-nums}
.ctl{display:flex;gap:8px;margin-top:10px;align-items:center}
.ctl button{flex:1;height:48px;border-radius:24px;background:var(--s2);display:flex;align-items:center;justify-content:center}
.ctl button svg{width:22px;height:22px;fill:currentColor}
.ctl #pp{flex:1.5;height:54px;border-radius:27px;background:var(--a);color:var(--on)}
.ctl #pp svg{width:26px;height:26px}
.link{font-size:14px;color:var(--mu);margin-top:10px;display:inline-block;padding:4px 0}

/* ---------- segmented */
.seg{display:flex;background:var(--s1);border:1px solid var(--ln);border-radius:16px;padding:3px;margin:0 0 12px}
.seg button{flex:1;height:36px;border-radius:13px;font-size:14px;font-weight:600;color:var(--mu)}
.seg button.on{background:var(--s2);color:var(--ink);box-shadow:inset 0 0 0 1px var(--ln)}

/* ---------- controller */
.shoulders{display:flex;justify-content:space-between;gap:8px;margin:4px 0 14px}
.shoulders div{display:flex;gap:8px}
.pill{height:40px;min-width:58px;padding:0 14px;border-radius:20px;background:var(--s1);border:1px solid var(--ln);font-size:14px;font-weight:650;color:#cfd5de;transition:transform .08s}
.dpad{position:relative;width:min(80vw,300px);aspect-ratio:1/1;margin:0 auto;border-radius:50%;background:radial-gradient(circle at 50% 40%,#1c212b,#13171e);border:1px solid var(--ln);box-shadow:0 18px 40px rgba(0,0,0,.45),inset 0 1px 0 rgba(255,255,255,.04);touch-action:none}
.dpad .ar{position:absolute;width:44px;height:44px;margin:-22px 0 0 -22px;display:flex;align-items:center;justify-content:center;color:var(--dim);transition:color .1s,transform .1s;pointer-events:none}
.dpad .ar svg{width:28px;height:28px;fill:currentColor}
.dpad .ar.dn{color:var(--a);transform:scale(1.18)}
#aU{left:50%;top:12%}#aD{left:50%;top:88%}#aL{left:12%;top:50%}#aR{left:88%;top:50%}
#aD svg{transform:rotate(180deg)}#aL svg{transform:rotate(-90deg)}#aR svg{transform:rotate(90deg)}
.ok{position:absolute;left:27%;top:27%;width:46%;height:46%;border-radius:50%;background:radial-gradient(circle at 50% 35%,#272e3b,#1b2029);border:1px solid #303747;display:flex;flex-direction:column;align-items:center;justify-content:center;pointer-events:none;transition:transform .08s,box-shadow .12s}
.ok b{font-size:20px;font-weight:750;letter-spacing:.04em}
.ok span{font-size:11px;color:var(--mu);margin-top:1px}
.ok.dn{transform:scale(.95);box-shadow:0 0 0 3px var(--a),0 0 28px -4px var(--a)}
.swipe{position:absolute;left:0;right:0;bottom:-26px;text-align:center;font-size:12px;color:var(--dim)}
.face{display:flex;align-items:center;justify-content:space-between;margin:40px 4px 0}
.sys{display:flex;flex-direction:column;gap:10px}
.sys button{display:flex;align-items:center;gap:10px;height:46px;padding:0 16px 0 12px;border-radius:23px;background:var(--s1);border:1px solid var(--ln);font-size:14px;font-weight:600;color:#cfd5de}
.sys svg{width:20px;height:20px;fill:currentColor}
.diamond{position:relative;width:162px;height:162px}
.fb{position:absolute;width:56px;height:56px;margin:-28px 0 0 -28px;border-radius:50%;background:var(--s1);border:1px solid var(--ln);display:flex;flex-direction:column;align-items:center;justify-content:center;font-weight:750;font-size:19px;transition:transform .08s,background .1s}
.fb span{font-size:9px;font-weight:600;color:var(--mu);margin-top:-2px;letter-spacing:.03em}
.fb.y{left:50%;top:17%;color:#f5c74d}.fb.x{left:17%;top:50%;color:#5fa8ff}.fb.b{left:83%;top:50%;color:#ff6b6b}.fb.a{left:50%;top:83%;color:#4fd08a}
.dn.pill,.dn.fb,.sys .dn{transform:scale(.93);background:var(--s2)}
.sticks{display:flex;gap:8px;justify-content:center;margin-top:16px}
.sticks .pill{height:34px;font-size:12px;color:var(--mu)}
.locked .dpad,.locked .face,.locked .shoulders,.locked .sticks{opacity:.35;pointer-events:none}

/* ---------- trackpad */
#pad{height:min(52vh,330px);border-radius:24px;background:radial-gradient(circle at 50% 30%,#181c24,#11141a);border:1px solid var(--ln);display:flex;align-items:center;justify-content:center;text-align:center;padding:0 30px;color:var(--dim);font-size:14px;touch-action:none}
#pad.on{border-color:var(--a);color:var(--mu)}
.row2{display:flex;gap:8px;margin-top:10px}
.row2 button{flex:1;height:50px;border-radius:25px;background:var(--s1);border:1px solid var(--ln);font-weight:650;font-size:15px}
.tools{display:flex;gap:8px;overflow-x:auto;margin:12px -16px 0;padding:2px 16px 6px;scrollbar-width:none}
.tools::-webkit-scrollbar{display:none}
.tools button{flex:none;height:40px;padding:0 14px;border-radius:20px;background:var(--s1);border:1px solid var(--ln);font-size:14px;color:#cfd5de;display:flex;align-items:center;gap:6px}
.tools button svg{width:18px;height:18px;fill:currentColor}

/* ---------- games */
.search{position:relative;margin:2px 0 10px}
.search svg{position:absolute;left:16px;top:15px;width:20px;height:20px;fill:var(--dim)}
.field{width:100%;height:50px;border-radius:25px;border:1px solid var(--ln);background:var(--s1);color:inherit;font:inherit;font-size:16px;padding:0 18px 0 46px;-webkit-appearance:none;outline:none}
.field:focus{border-color:var(--a)}
.chips{display:flex;gap:8px;overflow-x:auto;margin:0 -16px 12px;padding:0 16px;scrollbar-width:none}
.chips::-webkit-scrollbar{display:none}
.chips button{flex:none;height:34px;padding:0 15px;border-radius:17px;font-size:14px;font-weight:600;background:var(--s1);border:1px solid var(--ln);color:var(--mu)}
.chips button.on{color:var(--on);background:var(--a);border-color:var(--a)}
#grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px 10px}
@media (min-width:500px){#grid{grid-template-columns:repeat(4,1fr)}}
.tile{display:block;text-align:left;min-width:0;transition:transform .1s}
.tile:active{transform:scale(.96)}
.art{position:relative;aspect-ratio:2/3;border-radius:14px;overflow:hidden;background:linear-gradient(150deg,#232a37,#151920);border:1px solid var(--ln)}
.art img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;opacity:0;transition:opacity .25s}
.art img.in{opacity:1}
.art .ini{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-size:26px;font-weight:750;color:#4a5363;padding:8px;text-align:center}
.tile.off .art{filter:grayscale(.7) brightness(.6)}
.badge{position:absolute;left:6px;top:6px;font-size:10px;font-weight:750;letter-spacing:.04em;text-transform:uppercase;padding:3px 7px;border-radius:8px;background:var(--a);color:var(--on)}
.heart{position:absolute;right:6px;top:6px;width:18px;height:18px;fill:#ff6b8a;filter:drop-shadow(0 1px 2px rgba(0,0,0,.6))}
.nm{display:block;font-size:13px;font-weight:600;margin-top:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.sub{display:block;font-size:11px;color:var(--dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#gnote{text-align:center;color:var(--mu);font-size:14px;padding:30px 10px}
#more{height:1px}

/* ---------- sheet */
.sheet{position:fixed;inset:0;z-index:20;display:none}
.sheet.on{display:block}
.scrim{position:absolute;inset:0;background:rgba(4,6,10,.62)}
.panel{position:absolute;left:0;right:0;bottom:0;max-width:560px;margin:0 auto;background:var(--s1);border:1px solid var(--ln);border-bottom:0;border-radius:26px 26px 0 0;padding:10px 18px calc(18px + env(safe-area-inset-bottom));animation:up .24s cubic-bezier(.2,.9,.3,1)}
@keyframes up{from{transform:translateY(100%)}to{transform:none}}
.grab{width:40px;height:5px;border-radius:3px;background:var(--ln);margin:0 auto 14px}
.shtop{display:flex;gap:16px;align-items:flex-end;margin-bottom:18px}
.shtop .art{width:104px;flex:none;box-shadow:0 12px 30px rgba(0,0,0,.5)}
.shtop h2{margin:0 0 6px;font-size:22px;line-height:1.2;word-break:break-word}
.meta{font-size:13px;color:var(--mu);line-height:1.6}
.primary{width:100%;height:56px;border-radius:28px;background:var(--a);color:var(--on);font-weight:750;font-size:17px;display:flex;align-items:center;justify-content:center;gap:10px}
.primary svg{width:22px;height:22px;fill:currentColor}
.ghost{width:100%;height:50px;border-radius:25px;margin-top:8px;color:var(--mu);font-weight:600}

/* ---------- send */
.sendrow{display:flex;gap:10px}
.sendrow .field{padding-left:18px}
#go{flex:none;width:56px;height:50px;border-radius:25px;background:var(--a);color:var(--on);display:flex;align-items:center;justify-content:center}
#go svg{width:22px;height:22px;fill:currentColor}
#res{list-style:none;margin:6px 0 0;padding:0}
#res li{display:flex;gap:12px;align-items:center;padding:8px;margin:0 -8px;border-radius:16px}
#res li:active{background:var(--s1)}
#res img{width:124px;height:70px;border-radius:12px;object-fit:cover;background:var(--s1);flex:none}
#res b{display:block;font-size:15px;line-height:1.25;font-weight:600;word-break:break-word}
#res span{display:block;font-size:12px;color:var(--mu);margin-top:3px}
.how{margin-top:18px;display:grid;gap:10px}
.how div{display:flex;gap:12px;align-items:flex-start;font-size:14px;color:var(--mu)}
.how svg{flex:none;width:22px;height:22px;fill:var(--a);margin-top:1px}

/* ---------- keyboard */
#kb{position:fixed;inset:0;z-index:30;background:var(--bg);display:none;padding:calc(18px + env(safe-area-inset-top)) 16px 16px}
#kb.on{display:block}
#kb .in{max-width:560px;margin:0 auto}
#kt{margin:0 0 14px;font-size:24px;font-weight:700;word-break:break-word}
#kx{width:100%;min-height:96px;resize:none;border-radius:20px;border:2px solid var(--ln);background:var(--s1);color:inherit;font:inherit;font-size:21px;padding:14px 16px;-webkit-appearance:none;outline:none}
#kx:focus{border-color:var(--a)}
#kx.pw{-webkit-text-security:disc}
#sw{display:none;font-size:15px;color:var(--mu);margin:12px 0 0}
#sw input{margin-right:8px;width:18px;height:18px;vertical-align:-3px}
.kbtn{display:flex;gap:10px;margin-top:14px}
.kbtn button{flex:1;height:54px;border-radius:27px;font-weight:700;font-size:17px;background:var(--s2)}
.kbtn #kok{flex:2;background:var(--a);color:var(--on)}

/* ---------- tab bar + toast */
nav{position:fixed;left:0;right:0;bottom:0;z-index:10;background:rgba(18,21,28,.92);-webkit-backdrop-filter:blur(16px);backdrop-filter:blur(16px);border-top:1px solid var(--ln);padding-bottom:env(safe-area-inset-bottom)}
nav .in{max-width:560px;margin:0 auto;display:flex;height:var(--bar)}
nav button{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:3px;font-size:11px;font-weight:650;color:var(--dim);position:relative}
nav svg{width:26px;height:26px;fill:currentColor}
nav button.on{color:var(--ink)}
nav button.on svg{fill:var(--a)}
nav button em{position:absolute;top:9px;left:calc(50% + 9px);width:8px;height:8px;border-radius:50%;background:var(--a);display:none}
nav button em.on{display:block}
#toast{position:fixed;left:50%;bottom:calc(var(--bar) + 14px + env(safe-area-inset-bottom));transform:translate(-50%,20px);max-width:calc(100% - 32px);z-index:40;background:#252b36;color:var(--ink);border:1px solid #333b49;padding:11px 18px;border-radius:18px;font-size:14px;box-shadow:0 10px 30px rgba(0,0,0,.4);opacity:0;pointer-events:none;transition:opacity .2s,transform .2s;text-align:center}
#toast.on{opacity:1;transform:translate(-50%,0)}
#toast.bad{background:#3a2227;border-color:#5a2f37;color:#ffd0d0}
</style></head><body>

<header><div class='wrap'>
 <div class='top'><div class='brand'><i></i>Lanternfall</div><div id='conn'><b></b><span id='ct'>Connecting</span></div></div>
 <div id='now'><small id='nk'>On the TV</small><span id='nt'>...</span></div>
</div></header>

<main><div class='wrap'>

<!-- ===== Remote ===== -->
<section id='v_remote' class='view'>
 <div id='gameCard' class='card' style='display:none'>
  <div class='note'>Your controller drives the game now. The remote comes back when you close the game.</div>
 </div>
 <div id='oldCore' class='card note' style='display:none'>Update Lanternfall on the TV to use the controller from the phone.</div>

 <div id='ytCard' class='card' style='display:none'>
  <div id='ytNow'>
   <div class='t2' id='yt2' style='margin:0'></div>
   <div id='bar'><i id='bi'></i></div><div id='tm'><span id='t0'></span><span id='t1'></span></div>
   <div class='ctl'>
    <button id='pv' aria-label='Previous'><svg viewBox='0 0 24 24'><path d='M6 6h2v12H6zm3.5 6 8.5 6V6z'/></svg></button>
    <button id='bk' aria-label='Back 10 seconds'><svg viewBox='0 0 24 24'><path d='M11 18V6l-8.5 6zm.5-6 8.5 6V6z'/></svg></button>
    <button id='pp' aria-label='Play or pause'><svg viewBox='0 0 24 24' id='ppi'><path d='M6 19h4V5H6zm8-14v14h4V5z'/></svg></button>
    <button id='fw' aria-label='Forward 10 seconds'><svg viewBox='0 0 24 24'><path d='m4 18 8.5-6L4 6zm9-12v12l8.5-6z'/></svg></button>
    <button id='nx' aria-label='Next'><svg viewBox='0 0 24 24'><path d='m6 18 8.5-6L6 6zM16 6v12h2V6z'/></svg></button>
   </div>
   <button class='link' id='sp'>Stop video</button>
  </div>
  <div id='ytIdle' class='note'><p class='k'>YouTube</p>Nothing playing. Find something in Send.</div>
 </div>

 
 <div id='brSeg' class='seg' style='display:none'><button id='mPad'>Trackpad</button><button id='mCtl'>Controller</button></div>

 <div id='trackpad' style='display:none'>
  <div id='pad'>Drag to move the cursor &#183; tap to click &#183; two fingers to scroll</div>
  <div class='row2'><button id='pclick'>Click</button><button id='b_kbd'>Address bar</button></div>
  <div class='tools'>
   <button data-b='back'><svg viewBox='0 0 24 24'><path d='M20 11H7.8l5.6-5.6L12 4l-8 8 8 8 1.4-1.4L7.8 13H20z'/></svg>Back</button>
   <button data-b='forward'><svg viewBox='0 0 24 24'><path d='m12 4-1.4 1.4 5.6 5.6H4v2h12.2l-5.6 5.6L12 20l8-8z'/></svg>Forward</button>
   <button data-b='reload'><svg viewBox='0 0 24 24'><path d='M17.7 6.3A8 8 0 1 0 19.7 14h-2.1a6 6 0 1 1-1.4-6.2L13 11h7V4z'/></svg>Reload</button>
   <button data-b='zoomout'>Zoom &#8722;</button><button data-b='zoomin'>Zoom +</button>
   <button data-b='newtab'>New tab</button><button data-b='closetab'>Close tab</button><button data-b='home'>Start page</button>
  </div>
 </div>

 <div id='controller'>
  <div class='shoulders'>
   <div><button class='pill' data-p='TriggerLeft'>LT</button><button class='pill' data-p='LeftShoulder'>LB</button></div>
   <div><button class='pill' data-p='RightShoulder'>RB</button><button class='pill' data-p='TriggerRight'>RT</button></div>
  </div>
  <div class='dpad' id='dpad'>
   <div class='ar' id='aU'><svg viewBox='0 0 24 24'><path d='M12 6 5 15h14z'/></svg></div>
   <div class='ar' id='aD'><svg viewBox='0 0 24 24'><path d='M12 6 5 15h14z'/></svg></div>
   <div class='ar' id='aL'><svg viewBox='0 0 24 24'><path d='M12 6 5 15h14z'/></svg></div>
   <div class='ar' id='aR'><svg viewBox='0 0 24 24'><path d='M12 6 5 15h14z'/></svg></div>
   <div class='ok' id='okb'><b>A</b><span>Select</span></div>
   <div class='swipe'>Tap the arrows or swipe &#183; tap the middle to select</div>
  </div>
  <div class='face'>
   <div class='sys'>
    <button data-p='Back'><svg viewBox='0 0 24 24'><path d='M4 4h10v10H4zm6 6h10v10H10z' opacity='.9'/></svg>View</button>
    <button data-p='Start'><svg viewBox='0 0 24 24'><path d='M4 6h16v2H4zm0 5h16v2H4zm0 5h16v2H4z'/></svg>Menu</button>
   </div>
   <div class='diamond'>
    <button class='fb y' data-p='Y'>Y</button>
    <button class='fb x' data-p='X'>X</button>
    <button class='fb b' data-p='B'>B<span>Back</span></button>
    <button class='fb a' data-p='A'>A</button>
   </div>
  </div>
  <div class='sticks'><button class='pill' data-p='LeftStick'>L3</button><button class='pill' data-p='RightStick'>R3</button></div>
 </div>
</section>

<!-- ===== Games ===== -->
<section id='v_games' class='view'>
 <div class='search'><svg viewBox='0 0 24 24'><path d='M15.5 14h-.8l-.3-.3A6.5 6.5 0 1 0 14 15.5l.3.3v.8l5 5 1.5-1.5zm-6 0a4.5 4.5 0 1 1 0-9 4.5 4.5 0 0 1 0 9'/></svg>
  <input id='gq' class='field' type='search' placeholder='Find a game' autocomplete='off' autocorrect='off' autocapitalize='none' spellcheck='false' enterkeyhint='search'></div>
 <div class='chips' id='gchips'><button data-f='recent'>Recent</button><button data-f='installed'>Installed</button><button data-f='fav'>Favorites</button><button data-f='all'>All games</button></div>
 <div id='grid'></div><div id='more'></div>
 <div id='gnote'>Loading your games...</div>
</section>

<!-- ===== Send ===== -->
<section id='v_send' class='view'>
 <form id='f' class='sendrow' autocomplete='off'>
  <input id='q' class='field' type='text' inputmode='url' enterkeyhint='go' autocomplete='off' autocorrect='off' autocapitalize='none' spellcheck='false'>
  <button id='go' type='submit' aria-label='Send'><svg viewBox='0 0 24 24'><path d='M3 20.5 21 12 3 3.5v6.6L15 12 3 13.9z'/></svg></button>
 </form>
 <div class='chips' style='margin-top:10px'><button id='cy' type='button'>Search YouTube</button><button id='cw' type='button'>Search the web</button></div>
 <ul id='res'></ul>
 <div class='how' id='how'>
  <div><svg viewBox='0 0 24 24'><path d='M10 15l5.2-3L10 9zm11.6-7.8A2.5 2.5 0 0 0 19.8 5.4C18.2 5 12 5 12 5s-6.2 0-7.8.4A2.5 2.5 0 0 0 2.4 7.2C2 8.8 2 12 2 12s0 3.2.4 4.8a2.5 2.5 0 0 0 1.8 1.8c1.6.4 7.8.4 7.8.4s6.2 0 7.8-.4a2.5 2.5 0 0 0 1.8-1.8c.4-1.6.4-4.8.4-4.8s0-3.2-.4-4.8'/></svg>Paste a YouTube link and it plays on the TV. Or search, then tap a video.</div>
  <div><svg viewBox='0 0 24 24'><path d='M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20m6.9 6h-3a15.7 15.7 0 0 0-1.3-3.6A8 8 0 0 1 18.9 8M12 4c.8 1.2 1.5 2.5 1.9 4h-3.8c.4-1.4 1.1-2.8 1.9-4M4.3 14a8.2 8.2 0 0 1 0-4h3.4a16.5 16.5 0 0 0 0 4zm.8 2h3a15.7 15.7 0 0 0 1.3 3.6A8 8 0 0 1 5.1 16m3-8h-3a8 8 0 0 1 4.3-3.6C8.8 5.5 8.4 6.7 8.1 8M12 20c-.8-1.2-1.5-2.5-1.9-4h3.8c-.4 1.4-1.1 2.8-1.9 4m2.3-6H9.7a14.7 14.7 0 0 1 0-4h4.6a14.7 14.7 0 0 1 0 4m.3 5.6c.6-1.1 1-2.3 1.3-3.6h3a8 8 0 0 1-4.3 3.6m1.7-5.6a16.5 16.5 0 0 0 0-4h3.4a8.2 8.2 0 0 1 0 4z'/></svg>Any other link opens in the TV's browser.</div>
  <div><svg viewBox='0 0 24 24'><path d='M20 5H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2m-9 3h2v2h-2zm0 3h2v2h-2zM8 8h2v2H8zm0 3h2v2H8zm-1 2H5v-2h2zm0-3H5V8h2zm9 7H8v-2h8zm0-4h-2v-2h2zm0-3h-2V8h2zm3 3h-2v-2h2zm0-3h-2V8h2z'/></svg>When a keyboard opens on the TV, it opens here too.</div>
 </div>
</section>

</div></main>

<div id='sheet' class='sheet'><div class='scrim' id='scrim'></div>
 <div class='panel'><div class='grab'></div>
  <div class='shtop'><div class='art' id='sart'></div><div><h2 id='sn'></h2><div class='meta' id='sm'></div></div></div>
  <button class='primary' id='splay'><svg viewBox='0 0 24 24'><path d='M8 5v14l11-7z'/></svg><span id='splayt'>Play on the TV</span></button>
  <button class='ghost' id='sclose'>Cancel</button>
 </div>
</div>

<div id='kb'><div class='in'>
 <p class='k'>Typing on the TV</p>
 <h2 id='kt'></h2>
 <textarea id='kx' autocomplete='off' autocorrect='off' spellcheck='false' autocapitalize='none' enterkeyhint='done'></textarea>
 <label id='sw'><input type='checkbox' id='show'>Show what I type</label>
 <div class='kbtn'><button id='kno' type='button'>Cancel</button><button id='kok' type='button'>Done</button></div>
</div></div>

<nav><div class='in'>
 <button data-v='remote'><svg viewBox='0 0 24 24'><path d='M7.5 6h9a5.5 5.5 0 0 1 5.4 6.6l-1 5a2.9 2.9 0 0 1-5 1.3L13.6 16h-3.2l-2.3 2.9a2.9 2.9 0 0 1-5-1.3l-1-5A5.5 5.5 0 0 1 7.5 6M7 9v1.5H5.5v2H7V14h2v-1.5h1.5v-2H9V9zm8.5 0a1.2 1.2 0 1 0 0 2.4 1.2 1.2 0 0 0 0-2.4m2.5 2.6a1.2 1.2 0 1 0 0 2.4 1.2 1.2 0 0 0 0-2.4'/></svg>Remote<em id='dotR'></em></button>
 <button data-v='games'><svg viewBox='0 0 24 24'><path d='M4 4h7v7H4zm9 0h7v7h-7zM4 13h7v7H4zm9 0h7v7h-7z'/></svg>Games</button>
 <button data-v='send'><svg viewBox='0 0 24 24'><path d='M3 20.5 21 12 3 3.5v6.6L15 12 3 13.9z'/></svg>Send</button>
</div></nav>
<div id='toast'></div>

<script>
(function(){
var base=location.pathname.replace(/\/+$/,'');
function $(i){return document.getElementById(i)}
function each(sel,fn){var l=document.querySelectorAll(sel);for(var i=0;i<l.length;i++)fn(l[i])}
function store(k,v){try{if(v===undefined)return localStorage.getItem('lf.'+k);localStorage.setItem('lf.'+k,v)}catch(e){return null}}
function call(m,p,b,cb){var x=new XMLHttpRequest();x.open(m,base+p,true);x.timeout=20000;
 x.onload=function(){var j=null;try{j=JSON.parse(x.responseText)}catch(e){}cb(x.status,j)};
 x.onerror=x.ontimeout=function(){cb(0,null)};x.send(b===null||b===undefined?null:b)}
var touched=false;window.addEventListener('pointerdown',function(){touched=true},true);
['gesturestart','gesturechange','gestureend'].forEach(function(n){document.addEventListener(n,function(e){e.preventDefault()},{passive:false})});
document.addEventListener('dblclick',function(e){e.preventDefault()},{passive:false});
document.addEventListener('touchmove',function(e){if(e.touches.length>1&&!e.target.closest('#pad'))e.preventDefault()},{passive:false});
function buzz(ms){try{if(touched&&navigator.vibrate)navigator.vibrate(ms||8)}catch(e){}}
var toastT=null,lastToast='',lastToastAt=0;
function toast(s,bad){if(!s)return;var now=Date.now();if(s===lastToast&&now-lastToastAt<2500)return;lastToast=s;lastToastAt=now;
 var t=$('toast');t.textContent=s;t.className='on'+(bad?' bad':'');clearTimeout(toastT);toastT=setTimeout(function(){t.className=bad?'bad':''},bad?3800:2400)}
function fail(j){toast(j&&j.error?j.error:'Could not reach the TV.',true)}
function fmt(s){s=Math.max(0,Math.floor(s||0));var h=Math.floor(s/3600),m=Math.floor(s%3600/60),r=s%60;return (h?h+':'+(m<10?'0':'')+m:m)+':'+(r<10?'0':'')+r}

// ================= state from the TV
var S={ctx:'',screen:'home'},lost=0,first=true;
var view=store('tab')||'remote',brMode=store('brmode')||'pad';
function setView(v){view=v;store('tab',v);
 each('nav button',function(b){b.className=b.getAttribute('data-v')===v?'on':''});
 each('.view',function(s){s.className='view'+(s.id==='v_'+v?' on':'')});
 if(v==='games')loadGames(false);
 if(v==='remote')$('dotR').className='';
 window.scrollTo(0,0)}
each('nav button',function(b){b.addEventListener('click',function(){buzz(6);setView(b.getAttribute('data-v'))})});

function headline(){
 var k='On the TV',t='Lanternfall';
 if(kbOn){k='On the TV';t='Typing'}
 else if(S.ctx==='youtube'){k='YouTube';t=S.playing?(S.title||'Playing'):'Browsing videos'}
 else if(S.ctx==='browser'){var h=(S.url||'').replace(/^https?:\/\/(www\.)?/,'').split('/')[0];k=h?'Browser \u00b7 '+h:'Browser';t=S.tab||h||'Start page'}
 else if(S.screen==='game'){k='Playing';t=S.game||'A game'}
 else if(S.screen==='profile'){k='On the TV';t='Who is playing?'}
 else if(S.screen==='menu'){k='On the TV';t='A menu is open'}
 else if(S.screen==='library'){k='Collection';t=S.sel||'Your games'}
 else {k='Home';t=S.sel||'Lanternfall'}
 $('nk').textContent=k;$('nt').textContent=t}

function layout(){
 var game=S.screen==='game',yt=S.ctx==='youtube',br=S.ctx==='browser',old=S.pad===false;
 $('gameCard').style.display=game?'block':'none';
 $('oldCore').style.display=old&&!game?'block':'none';
 $('ytCard').style.display=yt?'block':'none';
 $('brSeg').style.display=br?'flex':'none';
 var pad=br&&brMode==='pad';
 $('trackpad').style.display=pad?'block':'none';$('controller').style.display=pad?'none':'block';
 $('mPad').className=brMode==='pad'?'on':'';$('mCtl').className=brMode==='ctl'?'on':'';
 $('v_remote').className=$('v_remote').className.replace(/ ?locked/,'')+(game||old?' locked':'');
 headline()}
$('mPad').addEventListener('click',function(){brMode='pad';store('brmode','pad');layout()});
$('mCtl').addEventListener('click',function(){brMode='ctl';store('brmode','ctl');layout()});

function accent(c){if(!c||!/^#[0-9a-f]{6}$/i.test(c))return;var r=document.documentElement.style,v=parseInt(c.substr(1),16),
 l=(0.2126*(v>>16&255)+0.7152*(v>>8&255)+0.0722*(v&255))/255;
 if(l<0.12)return;r.setProperty('--a',c);r.setProperty('--on',l>0.55?'#0b0e14':'#ffffff')}

function ytPaint(){
 var p=!!S.playing;$('ytNow').style.display=p?'block':'none';$('ytIdle').style.display=p?'none':'block';
 if(!p)return;
 $('yt2').textContent=S.author||'';
 var d=S.d||0,t=S.t||0;$('bi').style.width=(d>0?Math.min(100,t/d*100):0)+'%';
 $('t0').textContent=fmt(t);$('t1').textContent=S.live?'LIVE':(d>0?fmt(d):'');
 $('ppi').innerHTML=S.paused?'<path d=\'M8 5v14l11-7z\'/>':'<path d=\'M6 19h4V5H6zm8-14v14h4V5z\'/>'}

function poll(){call('GET','/state',null,function(s,j){
 if(j&&j.ok){
  if(lost>2)toast('Connected to the TV again.');
  lost=0;$('conn').className='cok';$('ct').textContent='Connected';
  var was=S.ctx+'|'+S.screen;S=j;accent(j.accent);
  kbState(j.kb);
  if(was!==S.ctx+'|'+S.screen&&view!=='remote'&&!first)$('dotR').className='on';
  if(S.ctx==='youtube')ytPaint();
  layout();first=false;
  if(S.screen==='game'&&games)markRunning(S.gameId)}
 else if(++lost>2){$('conn').className='cbad';$('ct').textContent='Not connected'}
 setTimeout(poll,kbOn?450:1000)})}

// ================= controller: every press goes through the TV's own pad path, in order
var pq=[],pbusy=false;
function press(b){if(pq.length>6)return;pq.push(b);pump()}
function pump(){if(pbusy||!pq.length)return;pbusy=true;var b=pq.shift();
 call('POST','/pad',JSON.stringify({b:b}),function(s,j){pbusy=false;if(!(j&&j.ok)){pq=[];fail(j)}pump()})}
function holdable(el,fn,repeat){var t1=null,t2=null,down=false;
 function start(e){e.preventDefault();if(down)return;down=true;el.classList.add('dn');buzz();fn();
  if(repeat)t1=setTimeout(function(){t2=setInterval(fn,120)},420)}
 function end(){if(!down)return;down=false;el.classList.remove('dn');clearTimeout(t1);clearInterval(t2);t1=t2=null}
 el.addEventListener('pointerdown',start);el.addEventListener('pointerup',end);el.addEventListener('pointercancel',end);el.addEventListener('pointerleave',end);
 el.addEventListener('contextmenu',function(e){e.preventDefault()})}
each('[data-p]',function(b){var n=b.getAttribute('data-p');holdable(b,function(){press(n)},n==='LeftShoulder'||n==='RightShoulder'||n==='TriggerLeft'||n==='TriggerRight')});

// the round pad: the ring is four arrows (held = repeat), the middle is A; a swipe anywhere moves too, one step per stretch
(function(){
 var pad=$('dpad'),arrows={DPadUp:$('aU'),DPadDown:$('aD'),DPadLeft:$('aL'),DPadRight:$('aR')};
 var id=null,ox=0,oy=0,zone=null,swiped=false,rep1=null,rep2=null,lastDir=null,still=null;
 function lit(d,on){if(arrows[d])arrows[d].className='ar'+(on?' dn':'')}
 function dir(dx,dy){return Math.abs(dx)>Math.abs(dy)?(dx>0?'DPadRight':'DPadLeft'):(dy>0?'DPadDown':'DPadUp')}
 function step(d){lit(lastDir,false);lastDir=d;lit(d,true);buzz(7);press(d)}
 function stopRep(){clearTimeout(rep1);clearInterval(rep2);rep1=rep2=null}
 function startRep(d,wait){stopRep();rep1=setTimeout(function(){rep2=setInterval(function(){press(d)},120)},wait)}
 pad.addEventListener('pointerdown',function(e){if(id!==null)return;e.preventDefault();id=e.pointerId;try{pad.setPointerCapture(id)}catch(x){}
  var r=pad.getBoundingClientRect(),cx=r.left+r.width/2,cy=r.top+r.height/2,dx=e.clientX-cx,dy=e.clientY-cy;
  ox=e.clientX;oy=e.clientY;swiped=false;lastDir=null;
  if(Math.sqrt(dx*dx+dy*dy)<r.width*0.23){zone='ok';$('okb').className='ok dn'}
  else{zone=dir(dx,dy);step(zone);startRep(zone,420)}});
 pad.addEventListener('pointermove',function(e){if(e.pointerId!==id)return;
  var dx=e.clientX-ox,dy=e.clientY-oy;
  if(Math.sqrt(dx*dx+dy*dy)<34)return;
  if(zone==='ok'){$('okb').className='ok'}
  swiped=true;zone='swipe';ox=e.clientX;oy=e.clientY;var d=dir(dx,dy);step(d);startRep(d,480)});
 function up(e){if(e.pointerId!==id)return;id=null;stopRep();lit(lastDir,false);
  if(zone==='ok'&&!swiped){$('okb').className='ok';buzz(10);press('A')}
  zone=null}
 pad.addEventListener('pointerup',up);pad.addEventListener('pointercancel',up);
 pad.addEventListener('contextmenu',function(e){e.preventDefault()});
})();

// ================= YouTube controls
function ctl(c){buzz();call('POST','/ctl',JSON.stringify({cmd:c}),function(s,j){if(!(j&&j.ok))fail(j)})}
[['pp','toggle'],['bk','back'],['fw','forward'],['nx','next'],['pv','previous'],['sp','stop']].forEach(function(p){$(p[0]).addEventListener('click',function(){ctl(p[1])})});

// ================= browser: tools + trackpad (the cursor the right stick moves)
function bc(c){call('POST','/bctl',JSON.stringify({cmd:c}),function(s,j){if(!(j&&j.ok))fail(j)})}
each('[data-b]',function(b){var c=b.getAttribute('data-b');holdable(b,function(){bc(c)},false)});
holdable($('b_kbd'),function(){bc('address')},false);
(function(){
 var pad=$('pad'),mx=0,my=0,sy=0,busy=false,timer=null,one=null,t0=0,moved=0,multi=false,lastY=0,down=false;
 function send(a,dx,dy){busy=true;call('POST','/ptr',JSON.stringify({a:a,dx:dx,dy:dy}),function(s,j){busy=false;if(j&&!j.ok&&j.error)toast(j.error,true)})}
 function pump(){if(busy)return;var dx=Math.round(mx),dy=Math.round(my);
  if(dx||dy){mx-=dx;my-=dy;send('move',dx,dy);return}
  var d=Math.round(sy);if(d){sy-=d;send('scroll',0,d)}}
 function run(){if(!timer)timer=setInterval(pump,35);pad.className='on'}
 function stop(){setTimeout(function(){if(!down&&timer){clearInterval(timer);timer=null}},250);pad.className=''}
 function click(){buzz(10);call('POST','/ptr',JSON.stringify({a:'click',dx:0,dy:0}),function(s,j){if(j&&!j.ok&&j.error)toast(j.error,true)})}
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

// ================= games
var games=null,gAt=0,gLoading=false,gFilter=store('gf')||'recent',gList=[],gShown=0,gObs=null,cur=null;
function chips(){each('#gchips button',function(b){b.className=b.getAttribute('data-f')===gFilter?'on':''})}
each('#gchips button',function(b){b.addEventListener('click',function(){gFilter=b.getAttribute('data-f');store('gf',gFilter);chips();renderGames()})});
$('gq').addEventListener('input',function(){renderGames()});
$('gq').addEventListener('keydown',function(e){if(e.key==='Enter'){e.preventDefault();$('gq').blur()}});
function loadGames(force){
 if(games&&!force&&Date.now()-gAt<60000){return}
 if(gLoading)return;gLoading=true;if(!games)$('gnote').textContent='Loading your games...';
 call('GET','/games',null,function(s,j){gLoading=false;
  if(j&&j.ok){games=j.games||[];gAt=Date.now();renderGames()}
  else if(!games)$('gnote').textContent=j&&j.error?j.error:'Could not reach the TV.'})}
function pick(){var q=$('gq').value.replace(/^\s+|\s+$/g,'').toLowerCase(),l=[],i,g;
 for(i=0;i<games.length;i++){g=games[i];
  if(q&&g.name.toLowerCase().indexOf(q)<0)continue;
  if(!q){if(gFilter==='recent'&&!g.last)continue;if(gFilter==='installed'&&!g.inst)continue;if(gFilter==='fav'&&!g.fav)continue}
  l.push(g)}
 if(gFilter==='all'||q)l.sort(function(a,b){return a.name.localeCompare(b.name)});
 return l}
function initials(n){var w=n.replace(/[^A-Za-z0-9 ]/g,' ').split(/\s+/).filter(function(x){return x}),s='';
 for(var i=0;i<w.length&&s.length<3;i++)s+=w[i][0];return (s||n.substr(0,2)).toUpperCase()}
function artFor(g,el){el.innerHTML='';var ini=document.createElement('div');ini.className='ini';ini.textContent=initials(g.name);el.appendChild(ini);
 if(g.cover){var im=document.createElement('img');im.alt='';im.loading='lazy';im.decoding='async';im.onload=function(){im.className='in'};im.onerror=function(){im.remove()};im.src=base+'/cover/'+g.id;el.appendChild(im)}
 if(g.run){var b=document.createElement('div');b.className='badge';b.textContent='Playing';el.appendChild(b)}
 else if(g.fav){el.insertAdjacentHTML('beforeend','<svg class=\'heart\' viewBox=\'0 0 24 24\'><path d=\'M12 21.4 10.6 20C5.4 15.4 2 12.3 2 8.5 2 5.4 4.4 3 7.5 3c1.7 0 3.4.8 4.5 2.1A6 6 0 0 1 16.5 3C19.6 3 22 5.4 22 8.5c0 3.8-3.4 6.9-8.6 11.5z\'/></svg>')}}
function ago(ms){var d=(Date.now()-ms)/86400000;if(d<1)return 'today';if(d<2)return 'yesterday';if(d<30)return Math.floor(d)+' days ago';if(d<365)return Math.floor(d/30)+' months ago';return Math.floor(d/365)+' years ago'}
function hours(s){if(!s)return '';var h=s/3600;return h>=1?(h<10?h.toFixed(1).replace('.0',''):Math.round(h))+' h played':Math.max(1,Math.round(s/60))+' min played'}
function renderGames(){if(!games)return;gList=pick();gShown=0;$('grid').innerHTML='';
 var q=$('gq').value.replace(/^\s+|\s+$/g,'');
 $('gnote').textContent=gList.length?'':(q?'No game called that.':(gFilter==='recent'?'Nothing played yet. Try All games.':gFilter==='fav'?'No favorites yet.':gFilter==='installed'?'No installed games.':'No games in the library.'));
 more()}
function more(){var end=Math.min(gList.length,gShown+60),grid=$('grid');
 for(var i=gShown;i<end;i++)(function(g){var b=document.createElement('button');b.className='tile'+(g.inst||g.run?'':' off');
  var a=document.createElement('div');a.className='art';artFor(g,a);b.appendChild(a);
  var n=document.createElement('span');n.className='nm';n.textContent=g.name;b.appendChild(n);
  var su=document.createElement('span');su.className='sub';su.textContent=g.run?'Playing now':(g.inst?(g.last?ago(g.last):(g.src||'Installed')):'Not installed');b.appendChild(su);
  b.addEventListener('click',function(){buzz(6);openSheet(g)});grid.appendChild(b)})(gList[i]);
 gShown=end}
if('IntersectionObserver' in window){gObs=new IntersectionObserver(function(en){if(en[0].isIntersecting&&games&&gShown<gList.length)more()},{rootMargin:'600px'});gObs.observe($('more'))}
else window.addEventListener('scroll',function(){if(games&&gShown<gList.length&&window.innerHeight+window.scrollY>document.body.offsetHeight-800)more()});
function markRunning(id){var ch=false;for(var i=0;i<games.length;i++){var r=games[i].id===id;if(games[i].run!==r){games[i].run=r;ch=true}}if(ch&&view==='games')renderGames()}

function openSheet(g){cur=g;artFor(g,$('sart'));$('sn').textContent=g.name;
 var m=[];if(g.src)m.push(g.src);var h=hours(g.play);if(h)m.push(h);if(g.last)m.push('Last played '+ago(g.last));if(!g.inst&&!g.run)m.push('Not installed');
 $('sm').innerHTML='';m.forEach(function(x){var d=document.createElement('div');d.textContent=x;$('sm').appendChild(d)});
 var busy=S.screen==='game'&&!g.run;
 $('splayt').textContent=g.run?'Back to the game':(busy?'Another game is running':(g.inst?'Play on the TV':'Install it on the PC first'));
 $('splay').disabled=busy||(!g.inst&&!g.run);
 $('sheet').className='sheet on'}
function closeSheet(){$('sheet').className='sheet';cur=null}
$('scrim').addEventListener('click',closeSheet);$('sclose').addEventListener('click',closeSheet);
$('splay').addEventListener('click',function(){if(!cur)return;var g=cur;buzz(14);$('splay').disabled=true;
 call('POST','/launch',JSON.stringify({id:g.id}),function(s,j){$('splay').disabled=false;
  if(j&&j.ok){closeSheet();toast(g.run?'Back to '+g.name:'Starting '+g.name+' on the TV');setView('remote')}else fail(j)})});

// ================= send a link or words
var mode='',userMode=false;
function chipsSend(){var m=mode||(S.ctx==='browser'?'web':'yt');$('cy').className=m==='yt'?'on':'';$('cw').className=m==='web'?'on':'';
 $('q').placeholder=m==='web'?'Address or web search':'YouTube link or search'}
$('cy').addEventListener('click',function(){mode='yt';chipsSend()});
$('cw').addEventListener('click',function(){mode='web';chipsSend()});
$('q').addEventListener('focus',chipsSend);
function clearRes(){$('res').innerHTML='';$('how').style.display='grid'}
function play(id,title){toast('Starting on the TV...');
 call('POST','/play',JSON.stringify({id:id}),function(s,j){
  if(j&&j.ok){toast('Playing on the TV: '+(title||'video'));clearRes();$('q').value='';setView('remote')}else fail(j)})}
function listResults(list){var res=$('res');res.innerHTML='';$('how').style.display='none';
 for(var i=0;i<list.length;i++)(function(v){
  var li=document.createElement('li'),im=document.createElement('img'),d=document.createElement('div'),b=document.createElement('b'),s=document.createElement('span');
  im.src=v.thumb||'';im.alt='';b.textContent=v.title||'';s.textContent=(v.author||'')+(v.duration?'  \u00b7  '+v.duration:'');
  d.appendChild(b);d.appendChild(s);li.appendChild(im);li.appendChild(d);
  li.addEventListener('click',function(){buzz(8);play(v.id,v.title)});res.appendChild(li)})(list[i])}
function ytsearch(words){toast('Searching YouTube...');
 call('GET','/search?q='+encodeURIComponent(words),null,function(s,j){
  if(j&&j.ok){if(!j.results.length)toast('Nothing found for that.',true);else listResults(j.results)}else fail(j)})}
var sending=false;
function send(){var t=$('q').value.replace(/^\s+|\s+$/g,'');if(!t||sending)return;sending=true;
 var m=mode||(S.ctx==='browser'?'web':'yt');
 call('POST','/send',JSON.stringify({text:t,mode:m}),function(s,j){sending=false;
  if(!j){toast('Could not reach the TV. Is it on the same Wi-Fi?',true);return}
  if(!j.ok){fail(j);return}
  if(j.kind==='search'){ytsearch(j.q);return}
  $('q').value='';clearRes();toast(j.kind==='video'?'Playing on the TV.':(j.kind==='web'?'Searching on the TV.':'Opened on the TV.'));setView('remote')})}
$('f').addEventListener('submit',function(e){e.preventDefault();$('q').blur();send()});
$('q').addEventListener('paste',function(){setTimeout(function(){if(/^\s*(https?:\/\/|(www\.)?(youtu\.be|youtube\.com))\S*\s*$/i.test($('q').value))send()},60)});

// ================= the TV's keyboard
var kx=$('kx'),kbOn=false,kbKnown=-1,kbDirty=false,kbBusy=false,kbTimer=null,kbMulti=false,kbCfg='';
function kbSetup(k){var cfg=k.title+'|'+k.multi+'|'+k.secret;if(cfg===kbCfg)return;kbCfg=cfg;kbKnown=-1;kbDirty=false;
 $('kt').textContent=k.title||'Type here';kbMulti=!!k.multi;
 kx.style.height=kbMulti?'38vh':'96px';kx.setAttribute('autocapitalize',kbMulti?'sentences':'none');
 $('sw').style.display=k.secret?'block':'none';$('show').checked=false;kx.className=k.secret?'pw':''}
function kbState(k){
 if(k){var opening=!kbOn;kbOn=true;kbSetup(k);
  if(!kbDirty&&!kbBusy&&k.v!==kbKnown){kbKnown=k.v;if(kx.value!==k.text)kx.value=k.text}
  if(opening){$('kb').className='on';buzz(12);setTimeout(function(){kx.focus()},60)}}
 else if(kbOn)kbGone('')}
function kbSend(cb){if(kbBusy)return;kbBusy=true;kbDirty=false;
 call('POST','/kb/text',JSON.stringify({text:kx.value}),function(s,j){kbBusy=false;
  if(s===410){kbGone('The keyboard closed on the TV.');return}
  if(j&&j.v!==undefined)kbKnown=j.v;if(kbDirty)kbSend();if(cb)cb()})}
function kbFlush(cb){if(!kbDirty&&!kbBusy){cb();return}if(kbBusy){setTimeout(function(){kbFlush(cb)},60);return}kbSend(function(){kbFlush(cb)})}
function kbGone(msg){kbOn=false;kbCfg='';kx.blur();$('kb').className='';if(msg)toast(msg);headline()}
function kbFinish(path,msg){kbFlush(function(){call('POST',path,'',function(s,j){
 if(j&&j.ok)kbGone(msg);else if(s===410)kbGone('The keyboard closed on the TV.');else toast('Could not reach the TV. Try again.',true)})})}
kx.addEventListener('input',function(){kbDirty=true;clearTimeout(kbTimer);kbTimer=setTimeout(function(){kbSend()},120)});
kx.addEventListener('keydown',function(e){if(!kbMulti&&(e.key==='Enter'||e.keyCode===13)){e.preventDefault();$('kok').click()}});
$('kok').addEventListener('click',function(){buzz(10);kbFinish('/kb/done','Sent to the TV.')});
$('kno').addEventListener('click',function(){kbFinish('/kb/cancel','')});
$('show').addEventListener('change',function(){kx.className=$('show').checked?'':'pw'});

chips();chipsSend();setView(view);layout();poll();
})();
</script></body></html>";
    }
}
