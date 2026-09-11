// ============================================================================
// ButterBatch.App / BridgePage.fs
// Web Serial API 授权页面（由本地 loopback 桥接服务提供）。
// 浏览器只在操作员显式选择串口后才获得串口句柄；读数带一次性令牌回传。
// ============================================================================
namespace ButterBatch.App

[<RequireQualifiedAccess>]
module BridgePage =

    let html (tokenScale: string) (tokenMeter: string) =
        ("""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<title>黄油批次 · 设备授权桥接</title>
<style>
 body{font-family:"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;margin:24px;color:#222;background:#fafafa}
 h1{font-size:18px}.card{background:#fff;border:1px solid #ddd;border-radius:10px;padding:16px;margin:12px 0;max-width:720px}
 button{padding:8px 14px;border:0;border-radius:6px;background:#2b6cb0;color:#fff;cursor:pointer;margin-right:8px}
 button.gray{background:#718096}button:disabled{background:#bbb;cursor:not-allowed}
 .ok{color:#2f855a}.bad{color:#c53030}.muted{color:#666;font-size:12px}
 pre{background:#f4f4f4;padding:8px;border-radius:6px;max-height:180px;overflow:auto}
 table{border-collapse:collapse;margin-top:8px}td,th{border:1px solid #ccc;padding:4px 10px;font-size:13px}
</style></head>
<body>
<h1>设备授权桥接（Web Serial API）</h1>
<p class="muted">本页仅运行在本机 loopback。须由操作员/质量人员点击“选择串口”完成授权后，读数才会进入本地系统；
关闭页面或点击“释放”即撤销授权。系统不提供任何配方或搅拌参数。</p>

<div class="card">
  <h3>台秤（称重 / 盐料 / 块重）</h3>
  <p class="muted">令牌：<code>""" + tokenScale + """</code> ｜ 报文约定：文本行，<code>ST,gs,+12.345</code>（稳定）或 <code>US,gs,+12.345</code>（未稳定）</p>
  <button id="scalePick">选择串口并连接台秤</button>
  <button id="scaleSend" disabled>发送当前稳定读数</button>
  <button id="scaleRelease" class="gray" disabled>释放授权</button>
  <span id="scaleState"></span>
  <pre id="scaleLog"></pre>
</div>

<div class="card">
  <h3>水分仪（水分结果）</h3>
  <p class="muted">令牌：<code>""" + tokenMeter + """</code> ｜ 报文约定：文本行，<code>OK,m,14.20</code>（完成）或 <code>BUSY,m,...</code></p>
  <button id="meterPick">选择串口并连接水分仪</button>
  <button id="meterSend" disabled>发送当前完成读数</button>
  <button id="meterRelease" class="gray" disabled>释放授权</button>
  <span id="meterState"></span>
  <pre id="meterLog"></pre>
</div>

<div class="card"><h3>已接收读数</h3><table id="tbl"><thead><tr><th>时间</th><th>设备</th><th>值</th><th>稳定</th><th>结果</th></tr></thead><tbody></tbody></table></div>

<script>
const POST = "/reading";
function log(el,s){el.textContent = (new Date().toLocaleTimeString()+ "  " + s + "\n") + el.textContent;}
function row(kind,v,st,res){
  const tr=document.createElement("tr");
  tr.innerHTML=`<td>${new Date().toLocaleTimeString()}</td><td>${kind}</td><td>${v}</td><td>${st?"是":"否"}</td><td>${res}</td>`;
  document.querySelector("#tbl tbody").prepend(tr);
}
async function setup(cfg){
  const $=id=>document.getElementById(id);
  if(!("serial" in navigator)){$(cfg.stateId).innerHTML='<span class="bad">当前浏览器不支持 Web Serial，请使用 Chrome/Edge。</span>';return;}
  let port=null,reader=null,closed=true,last=null;
  const setBtns=(connected)=>{$(cfg.sendId).disabled=!connected||!last;$(cfg.releaseId).disabled=!connected;$(cfg.pickId).disabled=connected;};
  async function connect(){
    port = await navigator.serial.requestPort();              // 授权点：必须用户手势
    await port.open({baudRate:cfg.baud});
    closed=false;setBtns(true);
    $(cfg.stateId).innerHTML='<span class="ok">已授权连接</span>';
    log($(cfg.logId),"串口已打开 ("+cfg.baud+")");
    const dec=new TextDecoderStream();port.readable.pipeThrough(dec).pipeTo(new WritableStream({write(line){
      line.split(/\r?\n/).forEach(t=>{t=t.trim();if(!t)return;
        log($(cfg.logId),"RX "+t);
        const p=t.split(",");
        if(p.length>=3){const stable=(p[0].toUpperCase()===(cfg.kind==="scale"?"ST":"OK"));
          last={stable:stable,value:parseFloat(p[2]),raw:t};setBtns(true);}});
    }})).catch(e=>{if(!closed)log($(cfg.logId),"读取结束: "+e);});
  }
  async function send(){
    if(!last)return;
    const body={token:cfg.token,kind:cfg.kind,value:last.value,stable:last.stable,raw:last.raw,at:new Date().toISOString()};
    const r=await fetch(POST,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});
    const txt=await r.text();
    row(cfg.kind==="scale"?"台秤":"水分仪",last.value,last.stable,(r.ok?"<span class=ok>已接收</span>":"<span class=bad>"+txt+"</span>"));
    log($(cfg.logId),"POST -> "+r.status+" "+txt);
  }
  async function release(){
    closed=true;try{reader&&reader.cancel();await port.close();}catch(e){}
    port=null;last=null;setBtns(false);$(cfg.stateId).innerHTML='<span>授权已释放</span>';
    await fetch("/revoke",{method:"POST",headers:{"Content-Type":"application/json"},
      body:JSON.stringify({token:cfg.token})});
  }
  $(cfg.pickId).onclick=()=>connect().catch(e=>{log($(cfg.logId),"错误: "+e);});
  $(cfg.sendId).onclick=()=>send().catch(e=>log($(cfg.logId),"发送错误: "+e));
  $(cfg.releaseId).onclick=()=>release().catch(e=>log($(cfg.logId),"释放错误: "+e));
}
setup({kind:"scale",token:SCALE_TOKEN,baud:9600,pickId:"scalePick",sendId:"scaleSend",releaseId:"scaleRelease",stateId:"scaleState",logId:"scaleLog"});
setup({kind:"moisture",token:METER_TOKEN,baud:9600,pickId:"meterPick",sendId:"meterSend",releaseId:"meterRelease",stateId:"meterState",logId:"meterLog"});
</script>
</body></html>"""
        ).Replace("__SCALE_TOKEN__", tokenScale).Replace("__METER_TOKEN__", tokenMeter)
