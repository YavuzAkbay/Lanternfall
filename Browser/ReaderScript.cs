// ReaderScript.cs - reader mode: the script that finds the article on a page and shows it alone, in big calm type, in the theme's colors.
// It scores the page's text containers (the usual "most paragraphs, fewest links" idea), copies the headings, paragraphs, lists, quotes and
// pictures of the winner as plain text and images into a full-screen layer, and hides the page behind it. window.__lfReader.off() puts
// the page back. No WPF or WebView2 here, so tools\tests\BrowserTools.Check.cs can build the script. C# 5 only.
using System;

namespace SeleneBrowser
{
    internal static class ReaderScript
    {
        // JavaScript with single quotes only (it sits in a C# string); %BG% %INK% %ACCENT% become the theme's colors
        private const string Template = @"(function(){
 if(window.__lfReader&&document.getElementById('lf-reader')) return 'ok:already';
 var BAD=/comment|share|social|related|sidebar|footer|nav|menu|promo|advert|cookie|banner|subscribe|newsletter|popup|modal|breadcrumb|widget|toolbar|pagination|recommend|sponsor/i;
 function words(e){return (e.innerText||e.textContent||'').replace(/\s+/g,' ').trim();}
 function bad(e){var s=(e.id||'')+' '+(typeof e.className==='string'?e.className:'');return BAD.test(s);}
 function shown(e){try{var r=e.getBoundingClientRect(),st=getComputedStyle(e);return st.display!=='none'&&st.visibility!=='hidden'&&(r.width>0||r.height>0);}catch(x){return false;}}
 var els=[],scores=[];
 function add(el,v){var i=els.indexOf(el);if(i<0){els.push(el);scores.push(v);}else scores[i]+=v;}
 var ps=document.querySelectorAll('p,pre,blockquote'),i,j,k;
 for(i=0;i<ps.length;i++){
  var p=ps[i];if(bad(p)||!shown(p))continue;
  var t=words(p);if(t.length<40)continue;
  var par=p.parentElement||document.body;
  var v=1+(t.split(',').length-1)+Math.min(Math.floor(t.length/100),3);
  add(par,v);if(par.parentElement)add(par.parentElement,v/2);
 }
 var best=null,bs=0;
 for(j=0;j<els.length;j++){
  var el=els[j],sc=scores[j],lt=0,as=el.querySelectorAll('a');
  for(k=0;k<as.length;k++)lt+=words(as[k]).length;
  var tl=words(el).length||1;
  sc=sc*(1-Math.min(0.9,lt/tl));
  if(bad(el))sc*=0.3;
  if(sc>bs){bs=sc;best=el;}
 }
 if(!best||bs<5)return 'none';
 var art=best.closest&&best.closest('article');
 if(art&&words(art).length>=words(best).length*0.8)best=art;
 var h1=document.querySelector('article h1,main h1,h1');
 var title=h1?words(h1):(document.title||'');
 var host=document.createElement('div');host.id='lf-reader';
 var css=document.createElement('style');
 css.textContent='#lf-reader{position:fixed;left:0;top:0;right:0;bottom:0;z-index:2147483647;overflow:auto;background:%BG%;color:%INK%;font:26px/1.65 Georgia,\'Segoe UI\',serif}'
  +'#lf-reader .in{max-width:900px;margin:0 auto;padding:7vh 40px 16vh}'
  +'#lf-reader h1{font:700 1.7em/1.25 \'Segoe UI\',sans-serif;margin:0 0 .6em}'
  +'#lf-reader h2,#lf-reader h3,#lf-reader h4{font:700 1.25em/1.3 \'Segoe UI\',sans-serif;margin:1.6em 0 .5em}'
  +'#lf-reader p,#lf-reader ul,#lf-reader ol{margin:0 0 1.1em}'
  +'#lf-reader li{margin:.3em 0}'
  +'#lf-reader blockquote{margin:1.2em 0;padding:.1em 0 .1em 1em;border-left:4px solid %ACCENT%;opacity:.9}'
  +'#lf-reader pre{white-space:pre-wrap;font:20px/1.5 Consolas,monospace;padding:.8em 1em;background:rgba(127,127,127,.18);border-radius:12px}'
  +'#lf-reader img{display:block;max-width:100%;height:auto;margin:1.2em auto;border-radius:12px}'
  +'#lf-reader figcaption{font-size:.7em;opacity:.7;text-align:center;margin-top:-.8em}'
  +'#lf-reader .src{font:600 .6em \'Segoe UI\',sans-serif;opacity:.6;margin-bottom:1.6em;color:%ACCENT%}';
 host.appendChild(css);
 var inner=document.createElement('div');inner.className='in';host.appendChild(inner);
 if(title){var th=document.createElement('h1');th.textContent=title;inner.appendChild(th);}
 var src=document.createElement('div');src.className='src';src.textContent=location.hostname;inner.appendChild(src);
 var blocks=best.querySelectorAll('h1,h2,h3,h4,p,ul,ol,pre,blockquote,figure,img');
 var count=0,wordCount=0;
 for(i=0;i<blocks.length;i++){
  var e=blocks[i],tag=e.tagName;
  if(bad(e)||!shown(e))continue;
  if(tag==='H1'&&h1&&e===h1)continue;
  var wrap=e.parentElement&&e.parentElement.closest&&e.parentElement.closest('ul,ol,blockquote,figure,pre');
  if(wrap&&best.contains(wrap)&&wrap!==e)continue;
  var n=null,txt;
  if(tag==='P'||tag==='H1'||tag==='H2'||tag==='H3'||tag==='H4'){
   txt=words(e);if(!txt||(tag==='P'&&txt.length<2))continue;
   n=document.createElement(tag==='H1'?'h2':tag.toLowerCase());n.textContent=txt;wordCount+=txt.split(' ').length;
  }else if(tag==='UL'||tag==='OL'){
   var lis=e.querySelectorAll(':scope > li');if(!lis.length)continue;
   n=document.createElement(tag.toLowerCase());
   for(k=0;k<lis.length;k++){var li=document.createElement('li');li.textContent=words(lis[k]);n.appendChild(li);}
  }else if(tag==='PRE'||tag==='BLOCKQUOTE'){
   txt=(tag==='PRE'?(e.innerText||e.textContent||''):words(e));if(!txt.trim())continue;
   n=document.createElement(tag.toLowerCase());n.textContent=txt;
  }else if(tag==='FIGURE'||tag==='IMG'){
   var im=tag==='IMG'?e:e.querySelector('img');
   var u=im&&(im.currentSrc||im.src);if(!u||!/^https?:/i.test(u)||(im.width&&im.width<200))continue;
   n=document.createElement('figure');n.style.margin='0';
   var ni=document.createElement('img');ni.src=u;n.appendChild(ni);
   var cap=tag==='FIGURE'?e.querySelector('figcaption'):null;
   if(cap&&words(cap)){var fc=document.createElement('figcaption');fc.textContent=words(cap);n.appendChild(fc);}
  }
  if(n){inner.appendChild(n);count++;}
 }
 if(count<2)return 'none';
 var saved={vis:document.body.style.visibility,ov:document.documentElement.style.overflow};
 try{document.querySelectorAll('video,audio').forEach(function(m){try{m.pause()}catch(x){}});}catch(x){}
 document.body.style.visibility='hidden';
 document.documentElement.style.overflow='hidden';
 document.documentElement.appendChild(host);
 window.__lfReader={off:function(){
  var h=document.getElementById('lf-reader');if(h&&h.parentNode)h.parentNode.removeChild(h);
  document.body.style.visibility=saved.vis;document.documentElement.style.overflow=saved.ov;window.__lfReader=null;}};
 return 'ok:'+count+':'+wordCount;
})()";

        /// <summary>The script with the theme's colors in it (#RRGGBB each).</summary>
        public static string Build(string backgroundHex, string inkHex, string accentHex)
        {
            return Template.Replace("%BG%", Safe(backgroundHex, "#0b0e14")).Replace("%INK%", Safe(inkHex, "#f2f4f8")).Replace("%ACCENT%", Safe(accentHex, "#7aa2ff"));
        }

        /// <summary>Find in page: counts the matches in the visible text and selects the next (or previous) one, scrolling to it. Returns {n, ok} as JSON.</summary>
        public static string FindScript(string query, bool backwards)
        {
            string q = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(query ?? "");
            return "(function(q,back){var n=0;try{var t=((document.body&&document.body.innerText)||'').toLowerCase(),s=q.toLowerCase(),i=t.indexOf(s);"
                + "while(i>=0&&n<9999){n++;i=t.indexOf(s,i+s.length);}}catch(e){}var ok=false;try{ok=window.find(q,false,back,true,false,false,false);}catch(e){}"
                + "return JSON.stringify({n:n,ok:ok});})(" + q + "," + (backwards ? "true" : "false") + ")";
        }

        private static string Safe(string hex, string fallback)
        {
            if (hex == null || hex.Length != 7 || hex[0] != '#') return fallback;
            for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(hex[i])) return fallback;
            return hex;
        }
    }
}
