using System.Text;

namespace ScWebPanel
{
    /// <summary>
    /// 网页控制台的前端资源。**全部内联在这里、不落磁盘**，所以不存在
    /// "忘了部署 dist/ 目录"这类问题 —— 编译进 DLL，装到哪都自带界面。
    ///
    /// 视觉：浅绿 Minecraft 风（对齐参考图）—— 米白底 + 白卡片 + 大圆角 + 深绿主色。
    /// 单页 + 原生 JS，没有构建步骤，改样式直接改这里的字符串。
    /// </summary>
    public static class WebPanelAssets
    {
        public static string IndexHtml(string title, bool useServerSentEvents)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n");
            sb.Append("<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
            sb.Append("<meta name=\"robots\" content=\"noindex,nofollow\">\n");
            sb.Append("<title>").Append(HtmlEscape(title)).Append("</title>\n");
            sb.Append("<style>\n");
            AppendCss(sb);
            sb.Append("</style>\n</head>\n<body>\n");
            AppendBody(sb, title);
            // 把"服务端是否开了 SSE"直接写进页面：省掉一次额外的 /api/config 往返
            sb.Append("<script>window.WB_SSE=").Append(useServerSentEvents ? "true" : "false").Append(";</script>\n");
            sb.Append("<script>\n");
            AppendJs(sb);
            sb.Append("</script>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static string HtmlEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // =====================================================================
        // 样式 —— 浅绿 MC 风
        // =====================================================================
        private static void AppendCss(StringBuilder sb)
        {
            sb.Append(@"
:root{
  --bg:#eef4e6; --card:#ffffff; --card-2:#f7faf2;
  --green:#4a7c2f; --green-dark:#3a6224; --green-light:#7fb069; --green-pale:#e8f2dd;
  --text:#2c3328; --text-dim:#7d8a75; --line:#e3ebd8;
  --red:#c94f4f; --amber:#d99a2b; --blue:#4a7fa5;
  --radius:14px; --radius-sm:9px;
  --shadow:0 1px 3px rgba(60,90,40,.07),0 6px 20px rgba(60,90,40,.05);
}
*{box-sizing:border-box}
html,body{margin:0;padding:0}
body{
  background:var(--bg); color:var(--text);
  font:14px/1.6 -apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei',sans-serif;
  /* 参考图那种极淡的竖细纹底 */
  background-image:repeating-linear-gradient(90deg,rgba(120,160,90,.035) 0 1px,transparent 1px 22px);
}
a{color:var(--green);text-decoration:none}
button{font-family:inherit}

/* ---------- 顶栏 ---------- */
.topbar{
  display:flex;align-items:center;gap:18px;
  background:var(--card);border-bottom:1px solid var(--line);
  padding:0 22px;height:56px;position:sticky;top:0;z-index:50;
}
.brand{display:flex;align-items:center;gap:9px;font-weight:700;font-size:16px;letter-spacing:.2px}
.brand .leaf{
  width:22px;height:22px;border-radius:6px;
  background:linear-gradient(135deg,var(--green-light),var(--green));
  display:grid;place-items:center;color:#fff;font-size:12px;
}
.nav{display:flex;gap:4px;margin-left:6px}
.nav button{
  background:none;border:0;padding:8px 14px;border-radius:var(--radius-sm);
  cursor:pointer;color:var(--text-dim);font-size:14px;transition:.15s;
}
.nav button:hover{background:var(--green-pale);color:var(--green-dark)}
.nav button.on{background:var(--green-pale);color:var(--green-dark);font-weight:600}
.topbar .right{margin-left:auto;display:flex;align-items:center;gap:12px;font-size:13px;color:var(--text-dim)}
.pill{
  display:inline-flex;align-items:center;gap:6px;background:var(--green-pale);
  color:var(--green-dark);border-radius:999px;padding:4px 11px;font-size:12px;font-weight:600;
}
.pill.warn{background:#fdf0dc;color:#8a5d12}
.pill.err{background:#fbe6e6;color:#8c3535}

/* ---------- 布局 ---------- */
.wrap{max-width:1180px;margin:0 auto;padding:20px 22px 60px}
.view{display:none}
.view.on{display:block}
.row{display:flex;gap:16px;flex-wrap:wrap}
.card{
  background:var(--card);border:1px solid var(--line);border-radius:var(--radius);
  padding:18px 20px;box-shadow:var(--shadow);
}
.card + .card{margin-top:16px}
.card h2{
  margin:0 0 14px;font-size:14px;font-weight:700;color:var(--text);
  display:flex;align-items:center;gap:8px;
}
.card h2 .sub{margin-left:auto;font-weight:400;font-size:12px;color:var(--text-dim)}

/* ---------- 统计块 ---------- */
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(168px,1fr));gap:16px}
.stat{background:var(--card);border:1px solid var(--line);border-radius:var(--radius);padding:16px 18px;box-shadow:var(--shadow)}
.stat .k{font-size:12px;color:var(--text-dim);margin-bottom:6px}
.stat .v{font-size:26px;font-weight:700;line-height:1.15;letter-spacing:-.5px}
.stat .v small{font-size:13px;font-weight:500;color:var(--text-dim);margin-left:3px}
.stat .s{font-size:12px;color:var(--text-dim);margin-top:3px}
.stat.good .v{color:var(--green)}
.stat.warn .v{color:var(--amber)}
.stat.bad .v{color:var(--red)}

/* ---------- 表格 ---------- */
.tablewrap{overflow:auto;border-radius:var(--radius-sm)}
table{width:100%;border-collapse:collapse;font-size:13px}
th{
  text-align:left;font-weight:600;color:var(--text-dim);font-size:12px;
  padding:9px 12px;border-bottom:1px solid var(--line);white-space:nowrap;
}
td{padding:11px 12px;border-bottom:1px solid var(--line);vertical-align:middle}
tr:last-child td{border-bottom:0}
tbody tr:hover{background:var(--card-2)}
.mono{font-family:ui-monospace,Consolas,monospace;font-size:12px}
.dim{color:var(--text-dim)}

.bar{height:7px;border-radius:99px;background:var(--green-pale);overflow:hidden;min-width:68px}
.bar i{display:block;height:100%;background:var(--green-light);border-radius:99px}
.bar.low i{background:var(--red)}
.bar.mid i{background:var(--amber)}

/* ---------- 按钮 ---------- */
.btn{
  border:1px solid var(--line);background:var(--card);color:var(--text);
  padding:6px 13px;border-radius:var(--radius-sm);cursor:pointer;font-size:12.5px;
  transition:.15s;white-space:nowrap;
}
.btn:hover{background:var(--green-pale);border-color:var(--green-light)}
.btn.primary{background:var(--green);border-color:var(--green);color:#fff;font-weight:600}
.btn.primary:hover{background:var(--green-dark);border-color:var(--green-dark)}
.btn.danger{color:var(--red);border-color:#eccfcf}
.btn.danger:hover{background:#fbe6e6;border-color:var(--red)}
.btn:disabled{opacity:.45;cursor:not-allowed}
.btnrow{display:flex;gap:6px;flex-wrap:wrap}

/* ---------- 终端 ---------- */
.term{
  background:#1d2419;border-radius:var(--radius-sm);padding:14px 16px;
  height:calc(100vh - 330px);min-height:300px;overflow:auto;
  font:12.5px/1.65 ui-monospace,Consolas,'Cascadia Mono',monospace;
  color:#c9d6bd;
}
.term .ln{display:flex;gap:10px;white-space:pre-wrap;word-break:break-word}
.term .t{color:#6d7d60;flex:none}
.term .lv{flex:none;width:52px;font-weight:700;text-align:center;border-radius:4px;font-size:11px;height:19px;line-height:19px}
.term .m{flex:1;min-width:0}
.term .info .lv{color:#8fb87a;background:rgba(143,184,122,.13)}
.term .warn .lv{color:#e0b45c;background:rgba(224,180,92,.13)}
.term .warn .m{color:#e8cd8f}
.term .error .lv{color:#e58b8b;background:rgba(229,139,139,.14)}
.term .error .m{color:#f0a9a9}
.term .verbose .lv,.term .debug .lv{color:#7d8a75;background:rgba(125,138,117,.1)}
.term .verbose .m,.term .debug .m{color:#93a186}
.term .out .m{color:#a8c894}
.term .echo .m{color:#e3d08a}

.inputrow{display:flex;gap:9px;margin-top:11px}
.inputrow input{
  flex:1;padding:10px 13px;border:1px solid var(--line);border-radius:var(--radius-sm);
  font:13px/1 ui-monospace,Consolas,monospace;background:var(--card-2);color:var(--text);
}
.inputrow input:focus{outline:0;border-color:var(--green-light);background:#fff}

/* ---------- 背包 ---------- */
.inv{display:grid;grid-template-columns:repeat(auto-fill,minmax(76px,1fr));gap:9px}
.slot{
  background:var(--card-2);border:1px solid var(--line);border-radius:var(--radius-sm);
  padding:9px 7px;text-align:center;position:relative;min-height:76px;
  display:flex;flex-direction:column;align-items:center;justify-content:center;gap:4px;
}
.slot .nm{font-size:11px;line-height:1.3;color:var(--text);word-break:break-all}
.slot .ct{font-size:12px;font-weight:700;color:var(--green-dark)}
.slot .badge{
  position:absolute;top:4px;left:5px;font-size:9.5px;color:var(--text-dim);
}
.slot .ic{
  width:26px;height:26px;border-radius:6px;display:grid;place-items:center;
  font-size:12px;font-weight:700;color:#fff;background:var(--green-light);
}
.slot.empty{opacity:.4}

/* ---------- 登录 ---------- */
.login{
  min-height:100vh;display:grid;place-items:center;padding:20px;
}
.login .box{
  background:var(--card);border:1px solid var(--line);border-radius:18px;
  padding:34px 32px;width:100%;max-width:372px;box-shadow:var(--shadow);
}
.login h1{margin:0 0 6px;font-size:19px;display:flex;align-items:center;gap:9px}
.login p{margin:0 0 22px;color:var(--text-dim);font-size:13px}
.login input{
  width:100%;padding:11px 13px;border:1px solid var(--line);border-radius:var(--radius-sm);
  font-size:14px;margin-bottom:12px;background:var(--card-2);color:var(--text);
}
.login input:focus{outline:0;border-color:var(--green-light);background:#fff}
.login button{
  width:100%;padding:11px;border:0;border-radius:var(--radius-sm);background:var(--green);
  color:#fff;font-size:14px;font-weight:600;cursor:pointer;
}
.login button:hover{background:var(--green-dark)}
.login .err{color:var(--red);font-size:13px;min-height:19px;margin-top:9px}

.hint{font-size:12px;color:var(--text-dim);margin-top:9px}
.empty{padding:32px;text-align:center;color:var(--text-dim);font-size:13px}
#toast{
  position:fixed;bottom:22px;left:50%;transform:translateX(-50%) translateY(70px);
  background:var(--text);color:#fff;padding:10px 19px;border-radius:99px;font-size:13px;
  opacity:0;transition:.25s;z-index:200;pointer-events:none;max-width:80vw;
}
#toast.on{opacity:1;transform:translateX(-50%) translateY(0)}
.chart{width:100%;height:172px;display:block}
.legend{display:flex;gap:16px;font-size:12px;color:var(--text-dim);justify-content:center;margin-top:7px}
.legend i{display:inline-block;width:9px;height:9px;border-radius:2px;margin-right:5px}
");
        }

        // =====================================================================
        // 结构
        // =====================================================================
        private static void AppendBody(StringBuilder sb, string title)
        {
            sb.Append(@"
<div id=""login"" class=""login"">
  <div class=""box"">
    <h1><span class=""leaf"">云</span>" + HtmlEscape(title) + @"</h1>
    <p>请输入管理口令以继续</p>
    <input id=""pw"" type=""password"" placeholder=""管理口令"" autocomplete=""current-password"">
    <button id=""loginBtn"">登录</button>
    <div class=""err"" id=""loginErr""></div>
  </div>
</div>

<div id=""app"" style=""display:none"">
  <div class=""topbar"">
    <div class=""brand""><span class=""leaf"">云</span>" + HtmlEscape(title) + @"</div>
    <div class=""nav"">
      <button data-view=""overview"" class=""on"">总览</button>
      <button data-view=""players"">玩家</button>
      <button data-view=""console"">控制台</button>
    </div>
    <div class=""right"">
      <span id=""conn"" class=""pill"">连接中</span>
      <button class=""btn"" id=""logout"">退出</button>
    </div>
  </div>
  <div class=""wrap"">

    <!-- ============ 总览 ============ -->
    <div class=""view on"" id=""v-overview"">
      <div class=""stats"" id=""stats""></div>

      <div class=""card"" style=""margin-top:16px"">
        <h2>性能曲线 <span class=""sub"" id=""chartSub""></span></h2>
        <canvas class=""chart"" id=""chart""></canvas>
        <div class=""legend"">
          <span><i style=""background:var(--green)""></i>TPS</span>
          <span><i style=""background:var(--amber)""></i>MSPT</span>
        </div>
      </div>

      <div class=""card"">
        <h2>世界信息</h2>
        <div id=""world"" class=""row""></div>
      </div>

      <div class=""card"">
        <h2>在线玩家 <span class=""sub"" id=""ovPlayersSub""></span></h2>
        <div id=""ovPlayers""></div>
      </div>
    </div>

    <!-- ============ 玩家管理 ============ -->
    <div class=""view"" id=""v-players"">
      <div class=""card"">
        <h2>在线玩家 <span class=""sub"">管理动作会立即生效</span></h2>
        <div id=""playerTable""></div>
      </div>
      <div class=""card"" id=""invCard"" style=""display:none"">
        <h2><span id=""invTitle"">背包</span>
          <span class=""sub""><button class=""btn"" id=""invClose"">关闭</button></span></h2>
        <div id=""invBody""></div>
      </div>
    </div>

    <!-- ============ 控制台 ============ -->
    <div class=""view"" id=""v-console"">
      <div class=""card"">
        <h2>控制台
          <span class=""sub"">
            <label style=""cursor:pointer""><input type=""checkbox"" id=""autoscroll"" checked style=""vertical-align:-1px""> 自动滚动</label>
            &nbsp;·&nbsp;
            <button class=""btn"" id=""logClear"">清屏</button>
          </span>
        </h2>
        <div class=""term"" id=""term""></div>
        <div class=""inputrow"">
          <input id=""cmd"" placeholder=""输入命令（不需要打 / 也可以），回车执行"" autocomplete=""off"">
          <button class=""btn primary"" id=""run"">发送</button>
        </div>
        <div class=""hint"">可用命令：<span id=""cmdHint"" class=""mono""></span></div>
      </div>
    </div>

  </div>
</div>
<div id=""toast""></div>
");
        }

        // =====================================================================
        // 脚本
        // =====================================================================
        private static void AppendJs(StringBuilder sb)
        {
            sb.Append(@"
'use strict';
var TOKEN = localStorage.getItem('wb_token') || '';
var VIEW = 'overview';
var LOG_CURSOR = 0;
var LOG_LINES = [];
var MAX_LINES = 2000;
var CHART_DATA = [];
var cmdCatalog = [];
var timer = null;

/* ---------------- 基础 ---------------- */
function $(id){ return document.getElementById(id); }
function esc(s){
  return String(s == null ? '' : s)
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
    .replace(/""/g,'&quot;').replace(/'/g,'&#39;');
}
function toast(msg){
  var t = $('toast'); t.textContent = msg; t.classList.add('on');
  clearTimeout(t._h); t._h = setTimeout(function(){ t.classList.remove('on'); }, 2200);
}
function fmtBytes(n){ return n >= 1024 ? (n/1024).toFixed(1)+' GB' : n.toFixed(0)+' MB'; }
function fmtDur(sec){
  sec = Math.max(0, Math.floor(sec||0));
  var d=Math.floor(sec/86400), h=Math.floor(sec%86400/3600), m=Math.floor(sec%3600/60);
  if(d) return d+'天'+h+'小时';
  if(h) return h+'小时'+m+'分';
  return m+'分';
}

/* ---------------- 网络 ---------------- */
function api(path, opts){
  opts = opts || {};
  opts.headers = opts.headers || {};
  if(TOKEN) opts.headers['Authorization'] = 'Bearer ' + TOKEN;
  if(opts.body && !opts.headers['Content-Type'])
    opts.headers['Content-Type'] = 'application/json';
  return fetch(path, opts).then(function(r){
    if(r.status === 401){ logoutLocal(); throw new Error('登录已失效，请重新登录'); }
    return r.json();
  });
}

function logoutLocal(){
  TOKEN = ''; localStorage.removeItem('wb_token');
  if(timer) clearInterval(timer);
  stopLogStream();
  $('app').style.display = 'none';
  $('login').style.display = 'grid';
}

/* ---------------- 登录 ---------------- */
function doLogin(){
  var pw = $('pw').value;
  if(!pw){ $('loginErr').textContent = '请输入口令'; return; }
  $('loginErr').textContent = '';
  fetch('/api/login', {
    method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ password: pw })
  }).then(function(r){ return r.json().then(function(d){ return {ok:r.ok, d:d}; }); })
    .then(function(res){
      if(!res.ok){ $('loginErr').textContent = res.d.message || '登录失败'; return; }
      TOKEN = res.d.token;
      localStorage.setItem('wb_token', TOKEN);
      $('pw').value = '';
      enterApp(res.d.title);
    })
    .catch(function(e){ $('loginErr').textContent = e.message || '网络错误'; });
}

function enterApp(title){
  if(title) document.title = title;
  $('login').style.display = 'none';
  $('app').style.display = 'block';
  switchView('overview');
  refresh();
  if(timer) clearInterval(timer);
  timer = setInterval(refresh, 2000);
  loadLogs();
}

/* ---------------- 视图切换 ---------------- */
function switchView(name){
  VIEW = name;
  var btns = document.querySelectorAll('.nav button');
  for(var i=0;i<btns.length;i++)
    btns[i].classList.toggle('on', btns[i].getAttribute('data-view') === name);
  var views = document.querySelectorAll('.view');
  for(var j=0;j<views.length;j++) views[j].classList.remove('on');
  var el = $('v-' + name); if(el) el.classList.add('on');
  refresh();
}

/* ---------------- 刷新 ---------------- */
function refresh(){
  setConn('ok','已连接');
  if(VIEW === 'overview') loadOverview();
  else if(VIEW === 'players') loadPlayers();
  else if(VIEW === 'console') loadLogs();
  loadMetrics();
}

function setConn(cls, text){
  var el = $('conn');
  el.className = 'pill' + (cls === 'err' ? ' err' : cls === 'warn' ? ' warn' : '');
  el.textContent = text;
}

function loadOverview(){
  api('/api/overview').then(function(d){
    if(!d.success) throw new Error(d.message || '读取失败');
    renderStats(d);
    renderWorld(d);
    if(d.players && d.players.online > 0) loadOverviewPlayers();
    else $('ovPlayers').innerHTML = '<div class=""empty"">当前没有玩家在线</div>';
  }).catch(function(e){ setConn('err', e.message); });
}

function renderStats(d){
  var m = d.metrics || {};
  var tps = m.tps == null ? 0 : m.tps;
  var mspt = m.mspt == null ? 0 : m.mspt;
  var p = d.process || {};
  var e = d.entities || {};
  var pl = d.players || {};

  var tpsCls = tps >= 19 ? 'good' : tps >= 15 ? 'warn' : 'bad';
  var msCls  = mspt <= 55 ? 'good' : mspt <= 90 ? 'warn' : 'bad';

  $('stats').innerHTML =
    stat('TPS', tps.toFixed(2), '目标 20.00', tpsCls) +
    stat('MSPT', mspt.toFixed(2) + '<small>ms</small>', '每 tick 毫秒数', msCls) +
    stat('在线玩家', pl.online + '<small>/ ' + (pl.max || '?') + '</small>', '服务器人数', '') +
    stat('实体', e.total, '生物 '+e.bodies+' · 掉落物 '+e.items, '') +
    stat('内存', fmtBytes(p.memoryMb || 0), '进程工作集', '') +
    stat('运行时长', p.uptime || '-', '线程 ' + (p.threads||0) + ' · ' + (p.cores||0) + ' 核', '');
}
function stat(k, v, s, cls){
  return '<div class=""stat ' + cls + '""><div class=""k"">' + esc(k) + '</div>' +
         '<div class=""v"">' + v + '</div><div class=""s"">' + esc(s) + '</div></div>';
}

function renderWorld(d){
  var w = d.world || {};
  $('world').innerHTML =
    kv('世界名称', w.name) + kv('种子', w.seed) + kv('默认模式', w.gameMode) +
    kv('游戏天数', '第 ' + w.day + ' 天') + kv('当前时段', w.dayPhase) +
    kv('天气', w.weather) + kv('世界时长', w.elapsedGameTime) +
    kv('机器', (d.process||{}).machine + ' · ' + (d.process||{}).runtime);
}
function kv(k, v){
  return '<div style=""flex:1 1 170px;min-width:150px"">' +
         '<div style=""font-size:12px;color:var(--text-dim)"">' + esc(k) + '</div>' +
         '<div style=""font-weight:600;margin-top:2px"">' + esc(v) + '</div></div>';
}

function loadMetrics(){
  api('/api/metrics').then(function(d){
    if(!d.success) return;
    CHART_DATA = d.samples || [];
    drawChart();
    $('chartSub').textContent = '近一分钟最低 TPS ' + d.minTps + ' · 最高 MSPT ' + d.maxMspt + 'ms';
  }).catch(function(){});
}

function drawChart(){
  var c = $('chart'); if(!c) return;
  var dpr = window.devicePixelRatio || 1;
  var w = c.clientWidth, h = c.clientHeight;
  if(c.width !== w*dpr || c.height !== h*dpr){
    c.width = w*dpr; c.height = h*dpr;
  }
  var g = c.getContext('2d');
  g.setTransform(dpr,0,0,dpr,0,0);
  g.clearRect(0,0,w,h);

  // 网格
  g.strokeStyle = 'rgba(120,160,90,.16)'; g.lineWidth = 1;
  for(var i=0;i<=4;i++){
    var y = 10 + (h-30) * i/4;
    g.beginPath(); g.moveTo(38,y); g.lineTo(w-38,y); g.stroke();
  }
  g.fillStyle = '#7d8a75'; g.font = '11px sans-serif';
  g.textAlign = 'right';
  g.fillText('20', 32, 14);
  g.fillText('10', 32, 10+(h-30)/2+4);
  g.fillText('0', 32, h-16);
  g.textAlign = 'left';
  g.fillText('100ms', w-36, 14);
  g.fillText('0ms', w-36, h-16);

  if(CHART_DATA.length < 2){ return; }

  var plotW = w-76, plotH = h-30;
  var n = CHART_DATA.length;

  function line(key, max, color){
    g.beginPath();
    for(var k=0;k<n;k++){
      var x = 38 + plotW * (k/(n-1));
      var v = Math.max(0, Math.min(max, CHART_DATA[k][key]));
      var y = 10 + plotH * (1 - v/max);
      k ? g.lineTo(x,y) : g.moveTo(x,y);
    }
    g.strokeStyle = color; g.lineWidth = 2;
    g.lineJoin = 'round'; g.stroke();
  }
  line('mspt', 100, '#d99a2b');
  line('tps', 20, '#4a7c2f');
}

function loadOverviewPlayers(){
  api('/api/players').then(function(d){
    if(!d.success) return;
    var list = d.players || [];
    $('ovPlayersSub').textContent = list.length + ' 人在线';
    $('ovPlayers').innerHTML = list.length ? playerTable(list, false) : '<div class=""empty"">当前没有玩家在线</div>';
  }).catch(function(){});
}

function loadPlayers(){
  api('/api/players').then(function(d){
    if(!d.success) throw new Error(d.message || '读取失败');
    var list = d.players || [];
    $('playerTable').innerHTML = list.length
      ? playerTable(list, true)
      : '<div class=""empty"">当前没有玩家在线</div>';
  }).catch(function(e){ toast(e.message); });
}

function playerTable(list, withActions){
  var rows = '';
  for(var i=0;i<list.length;i++){
    var p = list[i];
    var hp = Math.round((p.healthRatio||0)*100);
    var barCls = hp > 60 ? '' : hp > 30 ? 'mid' : 'low';
    rows += '<tr>' +
      '<td><strong>' + esc(p.name) + '</strong>' +
        (p.isAdmin ? ' <span class=""pill"" style=""padding:1px 7px;font-size:11px"">管理员</span>' : '') + '</td>' +
      '<td><div class=""bar ' + barCls + '""><i style=""width:' + hp + '%""></i></div>' +
        '<span class=""dim mono"">' + p.health + '/' + p.healthMax +
        (p.invulnerable ? ' 无敌' : '') + '</span></td>' +
      '<td class=""mono dim"">' + p.x + ', ' + p.y + ', ' + p.z + '</td>' +
      '<td>' + esc(p.gameMode) + '</td>' +
      '<td class=""mono"">' + (p.ping == null ? '<span class=""dim"">—</span>' : p.ping + 'ms') + '</td>' +
      '<td class=""dim"">' + esc(p.onlineGameTime) + '</td>' +
      (withActions ? '<td><div class=""btnrow"">' +
          '<button class=""btn"" data-act=""inventory"" data-g=""' + esc(p.guid) + '"">背包</button>' +
          '<button class=""btn"" data-act=""heal"" data-g=""' + esc(p.guid) + '"">回血</button>' +
          '<button class=""btn"" data-act=""gamemode"" data-g=""' + esc(p.guid) + '"">切换模式</button>' +
          '<button class=""btn"" data-act=""godmode"" data-g=""' + esc(p.guid) + '"">无敌</button>' +
          '<button class=""btn danger"" data-act=""kick"" data-g=""' + esc(p.guid) + '"">踢出</button>' +
        '</div></td>' : '') +
      '</tr>';
  }
  return '<div class=""tablewrap""><table><thead><tr>' +
    '<th>玩家</th><th>生命</th><th>坐标</th><th>模式</th><th>延迟</th><th>在线时长</th>' +
    (withActions ? '<th>操作</th>' : '') +
    '</tr></thead><tbody>' + rows + '</tbody></table></div>';
}

/* ---------------- 玩家动作 ---------------- */
document.addEventListener('click', function(ev){
  var btn = ev.target.closest ? ev.target.closest('button[data-act]') : null;
  if(!btn) return;
  var act = btn.getAttribute('data-act');
  var guid = btn.getAttribute('data-g');
  if(act === 'inventory'){ openInventory(guid, btn); return; }

  var label = { kick:'踢出', heal:'回血', kill:'击杀', clear:'清空背包',
                respawn:'送回重生点', gamemode:'切换游戏模式', godmode:'切换无敌' }[act] || act;
  if(!confirm('确定要对该玩家执行「' + label + '」吗？')) return;

  btn.disabled = true;
  api('/api/players/action', { method:'POST', body: JSON.stringify({ guid: guid, action: act }) })
    .then(function(d){
      btn.disabled = false;
      if(!d.success){ toast(d.message || '操作失败'); return; }
      toast(d.message || label + ' 成功');
      loadPlayers();
    })
    .catch(function(e){ btn.disabled = false; toast(e.message); });
});

function openInventory(guid, btn){
  btn.disabled = true;
  api('/api/inventory?guid=' + encodeURIComponent(guid)).then(function(d){
    btn.disabled = false;
    if(!d.success){ toast(d.message || '读取失败'); return; }
    $('invCard').style.display = 'block';
    $('invTitle').textContent = d.name + ' 的背包';
    $('invBody').innerHTML =
      invSection('快捷栏与主背包', d.main) +
      invSection('装备栏', d.armor) +
      (d.creative && hasSlots(d.creative) ? invSection('创造背包', d.creative) : '');
    $('invCard').scrollIntoView({ behavior:'smooth', block:'nearest' });
  }).catch(function(e){ btn.disabled = false; toast(e.message); });
}
function hasSlots(inv){ return inv && inv.slots && inv.slots.length > 0; }
function invSection(title, inv){
  if(!inv || !inv.slots || !inv.slots.length)
    return '<div style=""margin-bottom:14px""><div style=""font-size:12px;color:var(--text-dim);margin-bottom:8px"">' +
           esc(title) + ' · 空</div></div>';

  var html = '<div style=""margin-bottom:16px"">' +
    '<div style=""font-size:12px;color:var(--text-dim);margin-bottom:8px"">' +
    esc(title) + ' · ' + inv.slots.length + ' 种物品 / ' + inv.slotsCount + ' 格</div><div class=""inv"">';
  for(var i=0;i<inv.slots.length;i++){
    var s = inv.slots[i];
    var initial = (s.name || '?').charAt(0);
    html += '<div class=""slot"" title=""' + esc(s.name) + '（值 ' + s.value + '）"">' +
      '<span class=""badge"">' + s.index + '</span>' +
      '<span class=""ic"">' + esc(initial) + '</span>' +
      '<span class=""nm"">' + esc(s.name) + '</span>' +
      '<span class=""ct"">×' + s.count + '</span>' +
    '</div>';
  }
  return html + '</div></div>';
}

/* ---------------- 控制台 ---------------- */
// SSE 长连接；建立失败（或服务端关了 SSE）就自动退回轮询。
var SSE = null;
var SSE_FAILS = 0;

function startLogStream(){
  if(!window.WB_SSE || typeof EventSource === 'undefined'){ return; }
  if(SSE_FAILS >= 3){ return; }            // 连不上就别再折腾了，交给轮询
  try{ SSE = new EventSource('/api/logs/stream?since=' + LOG_CURSOR); }
  catch(e){ SSE_FAILS++; return; }

  SSE.onmessage = function(ev){
    SSE_FAILS = 0;
    var d;
    try{ d = JSON.parse(ev.data); }catch(e){ return; }
    if(!d || !d.success) return;
    ingestLines(d.lines || [], d.latest);
  };
  SSE.onerror = function(){
    // EventSource 自己会按 retry 重连；这里只累计失败次数，够了就切回轮询
    SSE_FAILS++;
    if(SSE_FAILS >= 3 && SSE){ SSE.close(); SSE = null; }
  };
}

function stopLogStream(){
  if(SSE){ SSE.close(); SSE = null; }
}

function ingestLines(lines, latest){
  if(!lines.length) return;
  for(var i=0;i<lines.length;i++){
    LOG_LINES.push(lines[i]);
    if(lines[i].id > LOG_CURSOR) LOG_CURSOR = lines[i].id;
  }
  if(latest && latest > LOG_CURSOR) LOG_CURSOR = latest;
  if(LOG_LINES.length > MAX_LINES) LOG_LINES = LOG_LINES.slice(-MAX_LINES);
  renderLogs();
}

function loadLogs(){
  // SSE 已经在工作时不再轮询（轮询只是兜底）
  if(SSE) { loadCommandHint(); return; }
  api('/api/logs?since=' + LOG_CURSOR).then(function(d){
    if(d.success) ingestLines(d.lines || [], d.latest);
    loadCommandHint();
    // 历史补齐、游标就位之后再接推送，保证时间线不重不漏
    startLogStream();
  }).catch(function(){ startLogStream(); });
}

function renderLogs(){
  var term = $('term');
  var html = '';
  for(var i=0;i<LOG_LINES.length;i++){
    var l = LOG_LINES[i];
    html += '<div class=""ln ' + l.level + '""><span class=""t"">' + esc(l.time) +
            '</span><span class=""lv"">' + esc(l.level.toUpperCase().slice(0,4)) +
            '</span><span class=""m"">' + esc(l.text) + '</span></div>';
  }
  term.innerHTML = html;
  if($('autoscroll').checked) term.scrollTop = term.scrollHeight;
}

// 本地回显（命令与结果）不占服务端序号：id 给 0，绝不能动 LOG_CURSOR，
// 否则下次用 since=LOG_CURSOR 拉日志会跳过服务端还没到的那几条。
function appendLocal(kind, text){
  LOG_LINES.push({ id: 0, time: new Date().toTimeString().slice(0,8), level: kind, text: text });
  if(LOG_LINES.length > MAX_LINES) LOG_LINES = LOG_LINES.slice(-MAX_LINES);
  renderLogs();
}

function loadCommandHint(){
  if(cmdCatalog.length) return;
  api('/api/commands').then(function(d){
    if(!d.success) return;
    cmdCatalog = d.commands || [];
    var names = cmdCatalog.map(function(c){ return '/' + c.name; });
    $('cmdHint').textContent = names.length ? names.join('  ') : '（未放行任何命令，见配置 WebPanel.AllowedCommandPrefixes）';
  }).catch(function(){});
}

function runCommand(){
  var input = $('cmd');
  var raw = input.value.trim();
  if(!raw) return;
  input.value = '';
  appendLocal('echo', '> ' + raw);

  api('/api/execute', { method:'POST', body: JSON.stringify({ command: raw }) })
    .then(function(d){
      if(d.output) appendLocal('out', d.output);
      if(d.error) appendLocal('error', d.error);
      else if(!d.success) appendLocal('error', d.message || '执行失败');
    })
    .catch(function(e){ appendLocal('error', e.message); });
}

/* ---------------- 事件绑定 ---------------- */
$('loginBtn').onclick = doLogin;
$('pw').addEventListener('keydown', function(e){ if(e.key === 'Enter') doLogin(); });
$('logout').onclick = function(){ api('/api/logout').catch(function(){}); logoutLocal(); };
$('invClose').onclick = function(){ $('invCard').style.display = 'none'; };
$('run').onclick = runCommand;
$('cmd').addEventListener('keydown', function(e){ if(e.key === 'Enter') runCommand(); });
$('logClear').onclick = function(){ LOG_LINES = []; renderLogs(); };
window.addEventListener('resize', drawChart);

var navBtns = document.querySelectorAll('.nav button');
for(var i=0;i<navBtns.length;i++){
  navBtns[i].onclick = function(){ switchView(this.getAttribute('data-view')); };
}

/* ---------------- 启动 ---------------- */
if(TOKEN){
  api('/api/overview').then(function(d){
    if(d && d.success) enterApp(d.title);
    else logoutLocal();
  }).catch(function(){ logoutLocal(); });
} else {
  logoutLocal();
}
");
        }
    }
}
