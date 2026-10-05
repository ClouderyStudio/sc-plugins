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
.pill.dim{background:#f1f3f2;color:var(--text-dim);font-weight:500}

/* 卡片内的小页签（封禁名单：账号 / IP 两个视图切换） */
.tabs{display:flex;gap:6px;margin:0 0 12px}
.tabs button{
  border:1px solid var(--line);background:#fff;border-radius:8px;padding:6px 14px;
  font-size:13px;cursor:pointer;color:var(--text-dim);font-family:inherit;
}
.tabs button:hover{border-color:var(--green)}
.tabs button.on{background:var(--green-pale);border-color:var(--green);color:var(--green-dark);font-weight:600}

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
.btn.warn{color:#8a5d12;border-color:#e8d5ac}
.btn.warn:hover{background:#fdf0dc;border-color:#c99a2e}
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

/* 背包里的可折叠区块（创造背包默认收起，少占地方） */
.invFold{
  margin-top:6px;border:1px solid var(--line);border-radius:var(--radius-sm);
  background:var(--card-2);overflow:hidden;
}
.invFold>summary{
  list-style:none;cursor:pointer;padding:10px 12px;
  font-size:12px;font-weight:600;color:var(--text);
  display:flex;align-items:center;gap:8px;user-select:none;
}
.invFold>summary::-webkit-details-marker{display:none}
.invFold>summary::before{
  content:""\25B8"";color:var(--text-dim);font-size:11px;transition:transform .15s;
}
.invFold[open]>summary::before{transform:rotate(90deg)}
.invFold>summary:hover{color:var(--green-dark)}
.invFold.foldEmpty>summary{color:var(--text-dim)}
.invFold .foldMeta{margin-left:auto;font-weight:400;color:var(--text-dim)}
.invFold .foldBody{padding:0 12px 4px;border-top:1px solid var(--line)}
.invFold .foldBody .inv{margin-top:12px}

/* ---------- 存档目录 ---------- */
.crumb{
  font-family:var(--mono);font-size:12px;color:var(--text-dim);
  padding:8px 10px;background:var(--card-2);border:1px solid var(--line);
  border-radius:var(--radius-sm);margin-bottom:12px;word-break:break-all;
}
.crumb a{color:var(--green-dark);cursor:pointer;text-decoration:none}
.crumb a:hover{text-decoration:underline}
.filelist{border:1px solid var(--line);border-radius:var(--radius-sm);overflow:hidden}
.filelist .frow{
  display:flex;align-items:center;gap:10px;padding:8px 12px;font-size:13px;
  border-bottom:1px solid var(--line);
}
.filelist .frow:last-child{border-bottom:none}
.filelist .frow:hover{background:var(--card-2)}
.filelist .frow .nm{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.filelist .frow .nm.dir{cursor:pointer;color:var(--green-dark);font-weight:600}
.filelist .frow .nm.dir:hover{text-decoration:underline}
.filelist .frow .sz{width:88px;text-align:right;color:var(--text-dim);font-size:12px}
.filelist .frow .tm{width:150px;text-align:right;color:var(--text-dim);font-size:12px}
.filelist .frow .kd{width:64px;color:var(--text-dim);font-size:11px;text-align:center}
.filepreview{
  margin:0;padding:14px;background:#1f2419;color:#d8e6c8;border-radius:var(--radius-sm);
  font-size:12px;line-height:1.55;font-family:var(--mono);overflow:auto;max-height:520px;
  white-space:pre-wrap;word-break:break-all;
}

/* ---------- 设置表单 ---------- */
.form{display:flex;flex-direction:column;gap:14px;max-width:620px}
.form label{display:flex;flex-direction:column;gap:6px;font-size:13px;color:var(--text)}
.form label.chk{flex-direction:row;align-items:center;gap:8px;cursor:pointer}
.form input[type=text],.form input[type=number],.form input[type=password],.form input:not([type]){
  padding:9px 11px;border:1px solid var(--line);border-radius:var(--radius-sm);
  font-size:13px;background:var(--card);color:var(--text);font-family:inherit;
}
.form input:focus{outline:none;border-color:var(--green-light);box-shadow:0 0 0 3px var(--green-pale)}
.form .tip{font-size:11.5px;color:var(--text-dim);line-height:1.5}

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
      <button data-view=""files"">存档</button>
      <button data-view=""settings"">设置</button>
    </div>
    <div class=""right"">
      <span id=""buildTag"" class=""pill dim"" title=""当前页面构建版本。若与实际功能对不上，点右边按钮强制刷新。""></span>
      <button class=""btn"" id=""hardReload"" title=""忽略浏览器缓存，重新拉取页面"">强制刷新</button>
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

      <!-- 封禁管理：账号名单与 IP 名单分开展示，IP 那一块自带 FRP 风险提示 -->
      <div class=""card"" id=""banCard"">
        <h2>封禁名单
          <span class=""sub"">
            <button class=""btn"" id=""banRefresh"">刷新</button>
            <button class=""btn"" id=""banAddIp"">手动封 IP</button>
          </span></h2>
        <div class=""tabs"" id=""banTabs"">
          <button data-bantab=""user"" class=""on"">账号封禁</button>
          <button data-bantab=""ip"">IP 封禁</button>
        </div>
        <div id=""banBody""><div class=""empty"">点「刷新」载入名单</div></div>
        <p class=""tip"" id=""banTip""></p>
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

    <!-- ============ 存档目录 ============ -->
    <div class=""view"" id=""v-files"">
      <div class=""card"">
        <h2>存档目录 <span class=""sub"">只读浏览服务端的数据目录（存档 / 日志 / 玩家数据）</span></h2>
        <div class=""crumb"" id=""filePath""></div>
        <div id=""fileList""><div class=""empty"">加载中…</div></div>
      </div>
      <div class=""card"" id=""fileViewCard"" style=""display:none"">
        <h2><span id=""fileViewName"">文件</span>
          <span class=""sub""><button class=""btn"" id=""fileViewClose"">关闭</button></span></h2>
        <div class=""hint"" id=""fileViewMeta""></div>
        <pre class=""filepreview"" id=""fileViewBody""></pre>
      </div>
    </div>

    <!-- ============ 设置 ============ -->
    <div class=""view"" id=""v-settings"">
      <div class=""card"">
        <h2>面板设置 <span class=""sub"">改完点「保存」，能热生效的会立即生效</span></h2>
        <div class=""form"">
          <label>面板标题<input id=""setTitle"" placeholder=""服务器控制台""></label>
          <label>监听地址
            <input id=""setHost"" placeholder=""127.0.0.1"">
            <span class=""tip"">127.0.0.1 = 仅本机；填 + 或 * = 允许外网（需要 URL ACL 授权，改这个务必先设强口令）</span>
          </label>
          <label>监听端口<input id=""setPort"" type=""number"" placeholder=""8080""></label>
          <label>登录有效期（分钟）<input id=""setSession"" type=""number""></label>
          <label>日志缓冲行数<input id=""setLogLines"" type=""number""></label>
          <label>登录失败上限<input id=""setMaxFail"" type=""number""></label>
          <label>失败后封禁秒数<input id=""setLockout"" type=""number""></label>
          <label>请求超时（秒）<input id=""setTimeout"" type=""number""></label>
          <label class=""chk""><input type=""checkbox"" id=""setSse""> 实时日志用 SSE 推送（关掉则前端轮询）</label>
          <label class=""chk""><input type=""checkbox"" id=""setLogActions""> 记录每次网页登录与命令执行</label>
          <label>允许的命令前缀
            <input id=""setAllowed"" placeholder=""help,list,admin,tp,kill,kick…"">
            <span class=""tip"">逗号分隔；这是网页终端的**唯一权限闸门**，留空 = 一条都不许执行</span>
          </label>
          <label>禁止的命令前缀
            <input id=""setDenied"" placeholder=""stop,ban"">
            <span class=""tip"">优先级高于白名单</span>
          </label>
        </div>
        <div class=""btnrow"" style=""margin-top:14px"">
          <button class=""btn primary"" id=""setSave"">保存设置</button>
          <button class=""btn"" id=""setReload"">重载配置</button>
          <span class=""dim mono"" id=""setStatus"" style=""margin-left:8px""></span>
        </div>
      </div>

      <div class=""card"">
        <h2>修改登录口令</h2>
        <div class=""form"">
          <label>旧口令<input id=""pwOld"" type=""password"" autocomplete=""current-password""></label>
          <label>新口令<input id=""pwNew"" type=""password"" autocomplete=""new-password"">
            <span class=""tip"">至少 6 位。改完所有登录会失效，需要重新登录。</span></label>
          <label class=""chk""><input type=""checkbox"" id=""pwHash"" checked> 只保存哈希（推荐，配置文件被看到也不泄露原口令）</label>
        </div>
        <div class=""btnrow"" style=""margin-top:14px"">
          <button class=""btn primary"" id=""pwSave"">更新口令</button>
        </div>
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
/* true = 令牌在 HttpOnly Cookie 里（JS 读不到，靠 Cookie 随请求自动带）。
   这种情况下去掉 Authorization 头，并把 credentials 设成 same-origin。
   SSE（EventSource）就是靠 Cookie 鉴权的 —— 它不能自定义 header，
   以前只能把令牌塞进 ?token=，那会让令牌进浏览器历史/Referer/服务器日志。
   有了 HttpOnly Cookie，SSE URL 里再也不需要带令牌。 */
var USE_COOKIE = !TOKEN;
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
  // Cookie 模式下不挂 Authorization：令牌在 HttpOnly Cookie 里，浏览器会自动带上。
  if(TOKEN && !USE_COOKIE) opts.headers['Authorization'] = 'Bearer ' + TOKEN;
  // same-origin：让浏览器在同源请求里带上会话 Cookie。
  opts.credentials = opts.credentials || 'same-origin';
  if(opts.body && !opts.headers['Content-Type'])
    opts.headers['Content-Type'] = 'application/json';
  return fetch(path, opts).then(function(r){
    if(r.status === 401){ logoutLocal(); throw new Error('登录已失效，请重新登录'); }
    return r.json();
  });
}

function logoutLocal(){
  TOKEN = ''; USE_COOKIE = true; localStorage.removeItem('wb_token');
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
  var err = $('loginErr');

  // 先取挑战值再登录。服务端 RequireChallenge 打开时必须带上，
  // 否则会返回《请求已过期，请刷新页面后重试》。
  fetch('/api/challenge', { method:'GET', credentials:'same-origin' })
    .then(function(r){
      // 挑战值接口自己失败（限流/网络）时必须在这里停住。
      // 否则会把失败响应当成《不需要挑战》继续去登录，最后报成口令错误 —— 误导排查。
      return r.json().then(function(d){
        if(!r.ok) throw new Error(d.message || '取挑战值失败，请稍后重试');
        return d;
      });
    })
    .then(function(ch){
      var body = { password: pw };
      if(ch && ch.required){
        if(!ch.challenge) throw new Error('取挑战值失败，请刷新页面后重试');
        body.challenge = ch.challenge;
      }
      return fetch('/api/login', {
        method:'POST', headers:{'Content-Type':'application/json'},
        // 同源 + Cookie 鉴权必须显式带上凭据，否则浏览器不会发送会话 Cookie。
        credentials:'same-origin',
        body: JSON.stringify(body)
      });
    })
    .then(function(r){ return r.json().then(function(d){ return {ok:r.ok, d:d}; }); })
    .then(function(res){
      if(!res.ok){ err.textContent = res.d.message || '登录失败'; return; }
      // 令牌已在 HttpOnly Cookie 里（服务端 cookie=true 时），JS 读不到也不需要读。
      // 这里只在服务端没启用 Cookie 时才把 token 留在内存里给 Authorization 头用。
      USE_COOKIE = !!res.d.cookie;
      if(!USE_COOKIE){
        TOKEN = res.d.token;
        localStorage.setItem('wb_token', TOKEN);
      } else {
        TOKEN = '';
        localStorage.removeItem('wb_token');
      }
      $('pw').value = '';
      enterApp(res.d.title);
    })
    .catch(function(e){ err.textContent = e.message || '网络错误'; });
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
  else if(VIEW === 'files') loadFiles(FILE_PATH);
  else if(VIEW === 'settings') loadSettings();
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

// 封 IP 的完整确认流程。
// 关键：先问服务端「这个地址上到底挂过几个账号」，再决定要不要封 ——
// 1 个 = 独占，封了干净；>1 = 家庭/校园网/网吧，封了就一起连坐。
function askBanIp(btn, guid, act){
  var peer = btn.getAttribute('data-ip') || '';
  btn.disabled = true;
  api('/api/connections?refresh=1').then(function(d){
    btn.disabled = false;
    var rec = null;
    if(d && d.success && d.ips){
      for(var i=0;i<d.ips.length;i++){
        if(d.ips[i].ip === peer){ rec = d.ips[i]; break; }
      }
    }
    var shareHint = '';
    if(rec){
      var n = rec.count || (rec.guids ? rec.guids.length : 0);
      var names = (rec.names || []).join('、');
      if(n > 1){
        shareHint = '\n\n⚠⚠ 这个地址在日志里关联了 ' + n + ' 个账号：' + names + '\n' +
                    '多半是家庭/校园网/网吧等多人共用，封它会把这些人一起挡在门外。\n' +
                    '建议改用「封禁账号」精确打击。';
      }else{
        shareHint = '\n\n✓ 这个地址在日志里只关联到 1 个账号（' + (names || '该玩家') + '），独占，封了不会连坐。';
      }
    }
    var hint = peer
      ? '\n\n该玩家当前的连接地址是 ' + peer + '。\n' +
        '⚠ 请确认你填的是玩家【真实公网 IP】，而不是服务端看到的连接地址（可能是代理机）。'
      : '\n\n（该玩家没有可读到的连接地址。）';
    var input = prompt(
      '要封禁哪个 IP？（可直接编辑下面预填的地址）\n' +
      '只接受 IP，不要带端口。' + hint + shareHint, peer);
    if(input === null) return;
    input = input.trim();
    if(!input){ toast('没有填 IP'); return; }
    // 手填的值和刚才查过的不一样时，用最新的统计再确认一遍
    if(input !== peer){
      api('/api/connections?refresh=1').then(function(d2){
        var r2 = null;
        if(d2 && d2.success && d2.ips){
          for(var j=0;j<d2.ips.length;j++){
            if(d2.ips[j].ip === input){ r2 = d2.ips[j]; break; }
          }
        }
        if(r2 && (r2.count || 0) > 1){
          if(!confirm('地址 ' + input + ' 在日志里关联了 ' + r2.count + ' 个账号（' +
                      (r2.names || []).join('、') + '）。\n\n' +
                      '封它会同时挡掉这些账号。真的要封吗？')) return;
        }
        submitBanIp(btn, guid, act, input);
      }).catch(function(){
        submitBanIp(btn, guid, act, input);
      });
      return;
    }
    if(rec && (rec.count || 0) > 1){
      if(!confirm('地址 ' + input + ' 关联了 ' + rec.count + ' 个账号（' +
                  (rec.names || []).join('、') + '）。\n\n' +
                  '继续封禁会把这些账号一起挡在门外。真的要这样吗？')) return;
    }
    submitBanIp(btn, guid, act, input);
  }).catch(function(e){
    btn.disabled = false;
    toast(e.message || '读取连接统计失败');
  });
}

function submitBanIp(btn, guid, act, ip){
  btn.disabled = true;
  api('/api/players/action', { method:'POST', body: JSON.stringify({ guid: guid, action: act, ip: ip }) })
    .then(function(d){
      btn.disabled = false;
      if(!d.success){ toast(d.message || '操作失败'); return; }
      toast(d.message || ('已封禁 IP ' + ip));
      loadPlayers();
      if(typeof loadBans === 'function' && $('banBody')) loadBans(BAN_TAB);
    })
    .catch(function(e){ btn.disabled = false; toast(e.message); });
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
      '<td class=""mono dim"">' + (p.peerIp
          ? esc(p.peerIp) + (p.peerIpBanned ? ' <span class=""pill warn"" style=""padding:1px 6px;font-size:11px"">已封</span>' : '')
          : '<span>—</span>') + '</td>' +
      (withActions ? '<td><div class=""btnrow"">' +
          '<button class=""btn"" data-act=""inventory"" data-g=""' + esc(p.guid) + '"">背包</button>' +
          '<button class=""btn"" data-act=""heal"" data-g=""' + esc(p.guid) + '"">回血</button>' +
          '<button class=""btn"" data-act=""fix"" data-g=""' + esc(p.guid) + '"">修复状态</button>' +
          '<button class=""btn"" data-act=""gamemode"" data-g=""' + esc(p.guid) + '"">切换模式</button>' +
          '<button class=""btn"" data-act=""godmode"" data-g=""' + esc(p.guid) + '"">无敌</button>' +
          '<button class=""btn"" data-act=""respawn"" data-g=""' + esc(p.guid) + '"">送回出生点</button>' +
          '<button class=""btn"" data-act=""kick"" data-g=""' + esc(p.guid) + '"">踢出</button>' +
          '<button class=""btn danger"" data-act=""ban"" data-g=""' + esc(p.guid) + '"">' +
            (p.isBanned ? '已在封禁名单' : '封禁账号') + '</button>' +
          '<button class=""btn warn"" data-act=""banip"" data-g=""' + esc(p.guid) +
            '"" data-ip=""' + esc(p.peerIp || '') + '"">封禁 IP</button>' +
        '</div></td>' : '') +
      '</tr>';
  }
  return '<div class=""tablewrap""><table><thead><tr>' +
    '<th>玩家</th><th>生命</th><th>坐标</th><th>模式</th><th>延迟</th><th>在线时长</th><th>连接地址</th>' +
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

  var label = { kick:'踢出', heal:'回血', kill:'击杀', clear:'清空背包', fix:'修复生存状态',
                respawn:'送回出生点', gamemode:'切换游戏模式', godmode:'切换无敌',
                ban:'封禁账号', unban:'解封账号', banip:'封禁 IP', unbanip:'解除 IP 封禁',
                banipuser:'记录账号 IP 并封禁', unbanipuser:'解除账号 IP 记录' }[act] || act;

  // ---- IP 封禁：必须管理员**手填** IP，面板只做候选提示，绝不代填代提交 ----
  // ⚠ 本服走 FRP，服务端看到的连接地址是代理机的、全服共用；拿它一键封 = 封全服。
  if(act === 'banip'){ askBanIp(btn, guid, act); return; }

  // 封禁是重动作，确认框要讲清楚「封的是账号不是 IP」，别让管理员误以为按 IP 封。
  var ask = '确定要对该玩家执行「' + label + '」吗？';
  if(act === 'ban')
    ask = '确定封禁该玩家的账号吗？\n\n' +
          '—— 本服的封禁按【社区账号】执行，与该玩家从哪个 IP 连进来无关，这是最稳的封禁方式。\n\n' +
          '封禁立即生效，该玩家会被踢下线且无法再进入。';
  if(act === 'banipuser')
    ask = '确定记录该玩家的 IP 并加入封禁表吗？\n\n' +
          '—— 这条走核心的 /ban ip user <账号id>：由核心按账号现场关联 IP，' +
          '不用你猜地址。之后该玩家再连进来会被挡。\n\n' +
          '⚠ 若本服经 FRP 转发，关联到的 IP 可能是全服共用的代理地址，' +
          '执行前请想清楚 —— 一旦误伤可以用「解除账号 IP 记录」回退。';
  if(act === 'unbanipuser')
    ask = '确定解除该玩家的 IP 记录吗？（/ban ip ruser）';
  if(!confirm(ask)) return;

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
    var creative = d.gameMode === 'Creative';
    $('invCard').style.display = 'block';
    $('invTitle').textContent = d.name + ' 的背包' +
      (d.gameMode && d.gameMode !== '-' ? '（' + (creative ? '创造' : '生存') + '模式）' : '');

    // 生存背包永远排在最前（快捷栏+主背包、护甲），创造背包放最后**且默认折叠**。
    // 主背包在不同模式下的语义不一样，标题跟着模式走，免得看的人误会。
    var mainTitle = creative ? '创造快捷栏（主背包）' : '快捷栏与主背包';
    var html = invSection(mainTitle, d.main) + invSection('装备栏', d.armor);

    // 创造背包：用 <details> 做成默认收起的一块，点标题才展开。
    // 折叠状态下标题栏仍然显示物品数量，不用展开也能一眼看出有没有东西。
    var cs = d.creative;
    if(cs && cs.slots && cs.slots.length){
      var counted = cs.slots.length + ' 种物品 / ' + cs.slotsCount + ' 格';
      html += '<details class=""invFold"">' +
        '<summary>创造背包 <span class=""foldMeta"">' + counted + '</span></summary>' +
        '<div class=""foldBody"">' + invSection('', cs, true) + '</div>' +
      '</details>';
    } else {
      html += '<details class=""invFold foldEmpty"">' +
        '<summary>创造背包 <span class=""foldMeta"">空</span></summary>' +
        '<div class=""foldBody""><div style=""font-size:12px;color:var(--text-dim);padding:8px 0"">' +
        '（无内容 —— 该玩家当前不在创造模式，或创造背包是空的）</div></div>' +
      '</details>';
    }

    $('invBody').innerHTML = html;
    $('invCard').scrollIntoView({ behavior:'smooth', block:'nearest' });
  }).catch(function(e){ btn.disabled = false; toast(e.message); });
}
function hasSlots(inv){ return inv && inv.slots && inv.slots.length > 0; }
// bare=true 时不再套一层标题栏（折叠区自带 summary），只出格子网格。
function invSection(title, inv, bare){
  if(!inv || !inv.slots || !inv.slots.length){
    if(bare) return '<div style=""font-size:12px;color:var(--text-dim);padding:8px 0"">空</div>';
    return '<div style=""margin-bottom:14px""><div style=""font-size:12px;color:var(--text-dim);margin-bottom:8px"">' +
           esc(title) + ' · 空</div></div>';
  }

  var head = bare ? '' :
    '<div style=""font-size:12px;color:var(--text-dim);margin-bottom:8px"">' +
    esc(title) + ' · ' + inv.slots.length + ' 种物品 / ' + inv.slotsCount + ' 格</div>';

  var html = '<div style=""margin-bottom:16px"">' + head + '<div class=""inv"">';
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
  // URL 里绝不带令牌：EventSource 不能自定义 header，以前只能拼 ?token=，
  // 那样令牌会进浏览器历史 / Referer / 服务器与反代日志。
  // 现在靠 HttpOnly 会话 Cookie —— 浏览器对同源 EventSource 会自动带上 Cookie。
  // withCredentials 只在跨域时才需要，这里同源，保持默认即可。
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

/* ---------------- 存档目录浏览 ---------------- */
var FILE_PATH = '';

function loadFiles(path){
  FILE_PATH = path || '';
  api('/api/files?path=' + encodeURIComponent(FILE_PATH)).then(function(d){
    if(!d.success){ $('fileList').innerHTML = '<div class=""empty"">' + esc(d.message || '读取失败') + '</div>'; return; }
    renderCrumb(d.path, d.parent);
    var list = d.entries || [];
    if(!list.length){ $('fileList').innerHTML = '<div class=""empty"">这个目录是空的</div>'; return; }

    var rows = '';
    for(var i=0;i<list.length;i++){
      var e = list[i];
      var esc_attr = esc(e.path);
      var nameCell = e.dir
        ? '<span class=""nm dir"" data-open=""' + esc_attr + '"">' + esc(e.name) + '/</span>'
        : '<span class=""nm"" title=""' + esc_attr + '"">' + esc(e.name) + '</span>';
      var actionCell = '';
      if(!e.dir && e.previewable)
        actionCell = '<button class=""btn"" data-view-file=""' + esc_attr + '"">查看</button>';
      rows += '<div class=""frow"">' +
        nameCell +
        '<span class=""kd"">' + esc(e.kind || '') + '</span>' +
        '<span class=""sz"">' + (e.sizeText || (e.size == null ? '—' : e.size)) + '</span>' +
        '<span class=""tm"">' + (e.modified || '—') + '</span>' +
        actionCell +
      '</div>';
    }
    var note = d.truncated ? '<div class=""hint"">目录内容过多，只显示前 400 项</div>' : '';
    $('fileList').innerHTML = note + '<div class=""filelist"">' + rows + '</div>';
  }).catch(function(e){ $('fileList').innerHTML = '<div class=""empty"">' + esc(e.message) + '</div>'; });
}

function renderCrumb(path, parent){
  var root = '<a data-open="""">数据目录</a>';
  if(!path){ $('filePath').innerHTML = root; return; }
  var parts = path.split('/');
  var acc = '';
  var chain = [];
  for(var i=0;i<parts.length;i++){
    acc = acc ? acc + '/' + parts[i] : parts[i];
    chain.push('<a data-open=""' + esc(acc) + '"">' + esc(parts[i]) + '</a>');
  }
  $('filePath').innerHTML = root + ' / ' + chain.join(' / ');
}

document.addEventListener('click', function(ev){
  var opener = ev.target.closest ? ev.target.closest('[data-open]') : null;
  if(opener){ loadFiles(opener.getAttribute('data-open')); return; }

  var viewer = ev.target.closest ? ev.target.closest('button[data-view-file]') : null;
  if(viewer){ openFile(viewer.getAttribute('data-view-file'), viewer); return; }
});

function openFile(path, btn){
  btn.disabled = true;
  api('/api/file?path=' + encodeURIComponent(path)).then(function(d){
    btn.disabled = false;
    if(!d.success){ toast(d.message || '读取失败'); return; }
    $('fileViewCard').style.display = 'block';
    $('fileViewName').textContent = d.name;
    $('fileViewMeta').textContent = d.path + '　·　' + fmtSize(d.size) + '　·　最后修改 ' + d.modified;
    $('fileViewBody').textContent = d.content;
    $('fileViewCard').scrollIntoView({ behavior:'smooth', block:'nearest' });
  }).catch(function(e){ btn.disabled = false; toast(e.message); });
}

function fmtSize(bytes){
  if(bytes == null) return '—';
  if(bytes < 1024) return bytes + ' B';
  if(bytes < 1048576) return (bytes/1024).toFixed(1) + ' KB';
  return (bytes/1048576).toFixed(2) + ' MB';
}

/* ---------------- 设置 ---------------- */
function loadSettings(){
  api('/api/settings').then(function(d){
    if(!d.success) throw new Error(d.message || '读取失败');
    $('setTitle').value = d.title || '';
    $('setHost').value = d.bindHost || '';
    $('setPort').value = d.port || '';
    $('setSession').value = d.sessionMinutes;
    $('setLogLines').value = d.logBufferLines;
    $('setMaxFail').value = d.maxLoginFailures;
    $('setLockout').value = d.lockoutSeconds;
    $('setTimeout').value = d.requestTimeoutSeconds;
    $('setSse').checked = !!d.useServerSentEvents;
    $('setLogActions').checked = !!d.logActions;
    $('setAllowed').value = d.allowedCommandPrefixes || '';
    $('setDenied').value = d.deniedCommandPrefixes || '';

    var status = '当前监听 ' + (d.running ? ('端口 ' + d.actualPort) : '未启动');
    if(d.hasPassword) status += '　·　口令：' + (d.passwordHashed ? '哈希' : '明文');
    else status += '　·　⚠ 未设口令';
    $('setStatus').textContent = status;
  }).catch(function(e){ toast(e.message); });
}

function saveSettings(){
  var payload = {
    title: $('setTitle').value,
    bindHost: $('setHost').value,
    port: parseInt($('setPort').value, 10) || 0,
    sessionMinutes: parseInt($('setSession').value, 10) || 0,
    logBufferLines: parseInt($('setLogLines').value, 10) || 0,
    maxLoginFailures: parseInt($('setMaxFail').value, 10) || 0,
    lockoutSeconds: parseInt($('setLockout').value, 10) || 0,
    requestTimeoutSeconds: parseInt($('setTimeout').value, 10) || 0,
    useServerSentEvents: $('setSse').checked,
    logActions: $('setLogActions').checked,
    allowedCommandPrefixes: $('setAllowed').value,
    deniedCommandPrefixes: $('setDenied').value
  };
  api('/api/settings', { method:'POST', body: JSON.stringify(payload) }).then(function(d){
    toast(d.message || '已保存');
    loadSettings();
  }).catch(function(e){ toast(e.message); });
}

function reloadSettings(){
  api('/api/reload', { method:'POST', body: '{}' }).then(function(d){
    toast(d.message || '已重载');
    loadSettings();
  }).catch(function(e){ toast(e.message); });
}

function changePassword(){
  var oldPw = $('pwOld').value;
  var newPw = $('pwNew').value;
  if(!newPw){ toast('请填写新口令'); return; }
  if(!confirm('确定要修改登录口令吗？改完所有登录都会失效，需要用新口令重新登录。')) return;

  api('/api/settings/password', { method:'POST', body: JSON.stringify({
    oldPassword: oldPw, newPassword: newPw, hash: $('pwHash').checked
  }) }).then(function(d){
    toast(d.message || '口令已更新');
    $('pwOld').value = ''; $('pwNew').value = '';
    // 口令已变，旧会话失效，直接回登录页
    setTimeout(logoutLocal, 1200);
  }).catch(function(e){ toast(e.message); });
}

/* ---------------- 封禁名单 ---------------- */
var BAN_TAB = 'user';   // 'user' 账号名单 / 'ip' IP 名单

// 页面构建版本。服务端每次返回的 ETag 都变，正常情况下浏览器不会缓存；
// 若仍看到旧界面（比如被中间代理缓存），点顶部「强制刷新」——
// 它会给地址加一个时间戳参数并走 location.replace，等价于跳过缓存重新拉。
var PAGE_BUILD = '20261006-1';

function banTipText(){
  if(BAN_TAB === 'ip')
    return '⚠ IP 封禁针对【网络出口地址】。本服若经 FRP 转发，服务端看到的地址是代理机的、' +
           '全体玩家共用 —— 封它等于封掉所有人。除非你确认某个 IP 就是攻击者本人的真实公网 IP，' +
           '否则请用「账号封禁」。';
  return '账号封禁认的是【社区账号 id】，与玩家从哪个 IP 连进来无关，是 FRP 环境下最可靠的封禁方式。';
}

function loadBans(tab){
  BAN_TAB = tab || BAN_TAB;
  var tabBtns = document.querySelectorAll('#banTabs button');
  for(var i=0;i<tabBtns.length;i++)
    tabBtns[i].className = tabBtns[i].getAttribute('data-bantab') === BAN_TAB ? 'on' : '';

  $('banTip').textContent = banTipText();
  $('banBody').innerHTML = '<div class=""empty"">读取中…</div>';

  // ⚠ 不能走 /api/execute：默认配置里 `ban` 在**禁止清单**里（DeniedCommandPrefixes 含 stop 与 ban），
  //   终端执行会被拦。名单读取改走玩家动作通道（ApplyPlayerAction 里直接借核心命令执行，
  //   不受网页终端白名单约束，因为它是「动作」而不是「用户手打的命令」）。
  var probe = document.querySelector('#playerTable button[data-g]');
  var guid = probe ? probe.getAttribute('data-g') : '00000000-0000-0000-0000-000000000000';
  var action = BAN_TAB === 'ip' ? 'baniplist' : 'banlist';

  api('/api/players/action', { method:'POST', body: JSON.stringify({ guid: guid, action: action }) })
    .then(function(d){
      if(!d.success){ $('banBody').innerHTML = '<div class=""empty"">' + esc(d.message || '读取失败') + '</div>'; return; }
      // 这条通道把命令回显并进了 message（见 WebPanelApi.ExecuteViaCommand），原样显示即可
      var out = (d.message || '').trim();
      $('banBody').innerHTML = out
        ? '<pre class=""filepreview mono"">' + esc(out) + '</pre>'
        : '<div class=""empty"">名单为空</div>';
    })
    .catch(function(e){
      $('banBody').innerHTML = '<div class=""empty"">' + esc(e.message) + '</div>';
    });
}

function addIpManually(){
  var input = prompt(
    '要封禁哪个 IP？（不要带端口）\n\n' +
    '只接受 IPv4 / IPv6 地址本身，填完面板还会查一遍这个地址上挂过几个账号。', '');
  if(input === null) return;
  input = input.trim();
  if(!input){ toast('没有填 IP'); return; }

  // 借用任意在线玩家作为 guid 载体（banip 只用得到 ip，不依赖具体玩家）
  var online = document.querySelector('#playerTable button[data-g]');
  var guid = online ? online.getAttribute('data-g') : '00000000-0000-0000-0000-000000000000';

  api('/api/connections?refresh=1').then(function(d){
    var rec = null;
    if(d && d.success && d.ips){
      for(var i=0;i<d.ips.length;i++){
        if(d.ips[i].ip === input){ rec = d.ips[i]; break; }
      }
    }
    if(rec && (rec.count || 0) > 1){
      if(!confirm('地址 ' + input + ' 在日志里关联了 ' + rec.count + ' 个账号（' +
                  (rec.names || []).join('、') + '）。\n\n' +
                  '这是多人共用的地址，封它会一起挡掉。真的要封吗？')) return;
    }else if(!confirm('确认封禁 IP「' + input + '」？\n\n该地址上的在线玩家会被立即断开。')) return;
    submitBanIp(null, guid, 'banip', input);
  }).catch(function(){
    if(!confirm('确认封禁 IP「' + input + '」？\n\n该地址上的在线玩家会被立即断开。')) return;
    submitBanIp(null, guid, 'banip', input);
  });
}

/* 强制刷新：给地址加一个时间戳参数再 replace，绕过一切缓存重新拉页面。
   location.replace 不会在历史里留记录，按返回键不会掉进刷新循环。 */
function hardReload(){
  try{
    var url = new URL(location.href);
    url.searchParams.set('_v', Date.now());
    location.replace(url.toString());
  }catch(e){
    // 极老的浏览器没有 URL API，退回字符串拼接
    var sep = location.href.indexOf('?') >= 0 ? '&' : '?';
    location.replace(location.href + sep + '_v=' + Date.now());
  }
}


$('loginBtn').onclick = doLogin;
$('pw').addEventListener('keydown', function(e){ if(e.key === 'Enter') doLogin(); });
$('logout').onclick = function(){ api('/api/logout').catch(function(){}); logoutLocal(); };
$('invClose').onclick = function(){ $('invCard').style.display = 'none'; };
$('fileViewClose').onclick = function(){ $('fileViewCard').style.display = 'none'; };
$('banRefresh').onclick = function(){ loadBans(); };
$('banAddIp').onclick = addIpManually;
$('hardReload').onclick = hardReload;
$('buildTag').textContent = '构建 ' + PAGE_BUILD;
var banTabBtns = document.querySelectorAll('#banTabs button');
for(var bt=0;bt<banTabBtns.length;bt++){
  banTabBtns[bt].onclick = function(){ loadBans(this.getAttribute('data-bantab')); };
}
$('setSave').onclick = saveSettings;
$('setReload').onclick = reloadSettings;
$('pwSave').onclick = changePassword;
$('run').onclick = runCommand;
$('cmd').addEventListener('keydown', function(e){ if(e.key === 'Enter') runCommand(); });
$('logClear').onclick = function(){ LOG_LINES = []; renderLogs(); };
window.addEventListener('resize', drawChart);

var navBtns = document.querySelectorAll('.nav button');
for(var i=0;i<navBtns.length;i++){
  navBtns[i].onclick = function(){ switchView(this.getAttribute('data-view')); };
}

/* ---------------- 启动 ---------------- */
// Cookie 模式下 TOKEN 为空，但 Cookie 可能还在（服务端还认）——
// 所以照样去问一次 overview，让服务端说了算。
if(TOKEN || USE_COOKIE){
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
