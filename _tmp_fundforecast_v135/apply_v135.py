from pathlib import Path

root = Path.cwd()

def replace(path, old, new):
    p = root / path
    text = p.read_text(encoding='utf-8')
    if old not in text:
        raise SystemExit(f'marker missing: {path}: {old[:80]!r}')
    p.write_text(text.replace(old, new), encoding='utf-8')

replace('src/fund_platform/__init__.py', '__version__ = "1.3.4"', '__version__ = "1.3.5"')
replace('packaging/FundForecastApp.iss', '#define MyAppVersion "1.3.4"', '#define MyAppVersion "1.3.5"')

p = root / 'src/fund_platform/static/chart-controls.mjs'
text = p.read_text(encoding='utf-8')
marker = "export const rangeOptions = period => isMinutePeriod(period) ? MINUTE_RANGES : NAV_RANGES;\n"
extra = """
const PERIOD_LABELS = {
  intraday: '分时', '5m': '5分钟', '15m': '15分钟', '30m': '30分钟',
  '60m': '60分钟', '1d': '日K', '1w': '周K', '1mo': '月K',
};

export const periodLabel = period => PERIOD_LABELS[period] || String(period || '');
export const supportsPeriod = (period, capabilities) =>
  !isMinutePeriod(period) || capabilities?.minute_periods !== false;
"""
if 'export const periodLabel' not in text:
    if marker not in text: raise SystemExit('chart-controls marker missing')
    text = text.replace(marker, marker + extra)
p.write_text(text, encoding='utf-8')

replace('src/fund_platform/static/api.js',
        "return request(`/v1/funds/${code}/chart?${params}`,{signal})",
        "return request(`/v1/funds/${code}/chart?${params}`,{signal,cache:'no-store'})")

p = root / 'src/fund_platform/static/index.html'
text = p.read_text(encoding='utf-8')
old = '<div class="chart-toolbar"><div id="periodControls" class="segmented" role="group" aria-label="图表周期"><button data-period="intraday">分时</button><button data-period="5m">5分钟</button><button data-period="15m">15分钟</button><button data-period="30m">30分钟</button><button data-period="60m">60分钟</button><button data-period="1d" class="active">日K</button><button data-period="1w">周K</button><button data-period="1mo">月K</button></div><div class="chart-actions">'
new = '<div class="chart-toolbar"><div id="periodControls" class="segmented" role="group" aria-label="图表周期"><button type="button" data-period="intraday" aria-pressed="false">分时</button><button type="button" data-period="5m" aria-pressed="false">5分钟</button><button type="button" data-period="15m" aria-pressed="false">15分钟</button><button type="button" data-period="30m" aria-pressed="false">30分钟</button><button type="button" data-period="60m" aria-pressed="false">60分钟</button><button type="button" data-period="1d" class="active" aria-pressed="true">日K</button><button type="button" data-period="1w" aria-pressed="false">周K</button><button type="button" data-period="1mo" aria-pressed="false">月K</button></div><span id="periodHint" class="period-hint" hidden></span><div class="chart-actions">'
if old not in text: raise SystemExit('index period marker missing')
p.write_text(text.replace(old, new), encoding='utf-8')

p = root / 'src/fund_platform/static/app.css'
text = p.read_text(encoding='utf-8')
text = text.replace('.chart-toolbar{min-height:43px;', '.chart-toolbar{position:relative;z-index:3;min-height:43px;')
if '.period-hint{' not in text:
    text += "\n.period-hint{flex:0 1 auto;padding:4px 7px;border:1px solid #f4b86055;border-radius:5px;background:#f4b86012;color:var(--amber);font-size:10px;white-space:nowrap}.segmented button.unsupported{opacity:.38;text-decoration:line-through;text-decoration-thickness:1px}.chart-toolbar button{pointer-events:auto}\n"
p.write_text(text, encoding='utf-8')

p = root / 'src/fund_platform/static/app.js'
text = p.read_text(encoding='utf-8')
text = text.replace("import {chartPresentation, isMinutePeriod, normalizeRange, rangeOptions} from './chart-controls.mjs';",
                    "import {chartPresentation, isMinutePeriod, normalizeRange, periodLabel, rangeOptions, supportsPeriod} from './chart-controls.mjs';")
text = text.replace('nextPollAt:null,theme:', 'nextPollAt:null,chartRequestId:0,chartCapabilities:null,theme:')
old = "function applyCapabilities(exchange){document.querySelectorAll('#periodControls button').forEach(button=>{if(isMinutePeriod(button.dataset.period))button.title=exchange?'需授权的 ETF 分钟成交源':'只可显示已授权的盘中估值参考，非实时净值';else button.title=''})}"
new = """function setActivePeriod(period){state.period=period;document.querySelectorAll('#periodControls button').forEach(button=>{const active=button.dataset.period===period;button.classList.toggle('active',active);button.setAttribute('aria-pressed',String(active))})}
function applyCapabilities(capabilities=null,fund=selectedFund()){
  if(capabilities)state.chartCapabilities=capabilities;
  const known=state.chartCapabilities;
  const hasMinute=known?.minute_periods!==false;
  document.querySelectorAll('#periodControls button').forEach(button=>{
    if(!isMinutePeriod(button.dataset.period)){button.disabled=false;button.title='';return}
    button.disabled=Boolean(known)&&!hasMinute;
    button.classList.toggle('unsupported',button.disabled);
    button.title=button.disabled
      ?(fund?.trade_mode==='EXCHANGE'?'未配置授权的 ETF 分时/分钟成交源':'场外基金没有分钟成交；未配置授权的盘中估值参考源')
      :(fund?.trade_mode==='EXCHANGE'?'ETF 分时/分钟成交行情':'盘中估值参考（非实时净值）');
  });
  const hint=$('periodHint');
  if(Boolean(known)&&!hasMinute){hint.hidden=false;hint.textContent=fund?.trade_mode==='EXCHANGE'?'分时/分钟需配置授权 ETF 成交源':'场外基金：分时/分钟需配置授权盘中估值源'}else hint.hidden=true
}"""
if old not in text: raise SystemExit('app capabilities marker missing')
text = text.replace(old, new)
text = text.replace("applyCapabilities(fund.trade_mode==='EXCHANGE');await Promise.all([loadChart(),loadAnalysis(),loadBacktest(),loadPredictionRecords()])",
                    "state.chartCapabilities=null;applyCapabilities(null,fund);await Promise.all([loadChart(),loadAnalysis(),loadBacktest(),loadPredictionRecords()])")
start = text.index('async function loadChart(){')
end = text.index('\nfunction schedulePolling', start)
new_load = """async function loadChart(){
  if(!state.selectedCode)return;
  if(state.chartAbort)state.chartAbort.abort();
  state.chartAbort=new AbortController();
  const requestId=++state.chartRequestId;
  const requestedCode=state.selectedCode,requestedPeriod=state.period,requestedRange=state.range;
  const label=periodLabel(requestedPeriod);
  clearPolling();setStatus(`正在切换 ${label}`,'paused');$('chartEmpty').hidden=true;
  try{
    const payload=await api.getChart(requestedCode,{period:requestedPeriod,range:requestedRange,start:state.rangeStart,end:state.rangeEnd},state.chartAbort.signal);
    if(requestId!==state.chartRequestId||requestedCode!==state.selectedCode||requestedPeriod!==state.period)return;
    state.chartMode=payload.data_mode;
    applyCapabilities(payload.capabilities,selectedFund());
    const presentation=chartPresentation(payload);
    chart.setMode(payload.data_mode);
    chart.setData(payload.points);
    const latest=payload.points.at(-1);
    $('chartEmpty').hidden=payload.points.length>0;
    $('chartNotice').hidden=!payload.reason;
    $('chartNotice').textContent=payload.reason||'';
    $('chartTimestamp').textContent=payload.quality?.event_time?`行情时间 ${shortTime(payload.quality.event_time)}`:payload.quality?.business_date?`业务日期 ${payload.quality.business_date}`:'暂无业务时间';
    $('dataSource').textContent=`数据源：${payload.quality?.source||'未接入'} · ${label} · ${payload.points.length}点`;
    $('chartQuote').innerHTML=latest?`<strong>${Number(latest.value).toFixed(4)}</strong><span>${label} · ${presentation.valueLabel}</span>`:`<strong>—</strong><span>${label} · ${isMinutePeriod(requestedPeriod)?'暂无可验证分钟数据':'暂无真实行情'}</span>`;
    if(payload.quality?.is_live){setStatus(`${presentation.statusLabel} · ${label} · ${payload.points.length}点`,'');schedulePolling(15000)}
    else if(payload.points.length){setStatus(`${payload.quality?.status==='stale'?'行情过期':payload.quality?.status==='delayed'?'行情延迟':presentation.statusLabel} · ${label} · ${payload.points.length}点`,'paused');$('nextRefresh').textContent=isMinutePeriod(requestedPeriod)?'等待行情更新':'按日更新'}
    else{setStatus(`${label} · ${payload.applicable?'已暂停':'不可用'}`,'paused');$('nextRefresh').textContent='—'}
    updateEvidence(payload);
  }catch(error){if(error.name==='AbortError'||requestId!==state.chartRequestId)return;reportFrontendError('CHART',error);chart.setData([]);$('chartEmpty').hidden=false;$('chartNotice').hidden=false;$('chartNotice').textContent=`${label} 加载失败：${error.message}`;setStatus(`${label} · 加载失败`,'paused')}
}"""
text = text[:start] + new_load + text[end:]
old_handler = "document.querySelectorAll('#periodControls button').forEach(button=>button.onclick=()=>{state.period=button.dataset.period;state.range=normalizeRange(state.period,state.range);state.rangeStart=null;state.rangeEnd=null;document.querySelectorAll('#periodControls button').forEach(x=>x.classList.toggle('active',x===button));syncRangeControl();loadChart()});"
new_handler = "document.querySelectorAll('#periodControls button').forEach(button=>button.onclick=()=>{const period=button.dataset.period;if(button.disabled||!supportsPeriod(period,state.chartCapabilities))return;setActivePeriod(period);state.range=normalizeRange(period,state.range);state.rangeStart=null;state.rangeEnd=null;syncRangeControl();$('chartNotice').hidden=false;$('chartNotice').textContent=`正在切换到 ${periodLabel(period)}…`;loadChart()});"
if old_handler not in text: raise SystemExit('app handler marker missing')
text = text.replace(old_handler, new_handler)
text = text.replace("syncRangeControl();\nPromise.all([loadMarkets(),loadHistory(),loadFunds()]);", "setActivePeriod(state.period);syncRangeControl();\nPromise.all([loadMarkets(),loadHistory(),loadFunds()]);")
p.write_text(text, encoding='utf-8')

for path in (root / 'tests').rglob('*.py'):
    text = path.read_text(encoding='utf-8')
    if '1.3.4' in text:
        path.write_text(text.replace('1.3.4', '1.3.5'), encoding='utf-8')

p = root / 'tests/frontend/chart-controls-v135.test.mjs'
p.write_text("""import assert from 'node:assert/strict';\nimport test from 'node:test';\nimport {periodLabel,supportsPeriod} from '../../src/fund_platform/static/chart-controls.mjs';\ntest('v135 labels and minute capability',()=>{assert.equal(periodLabel('1mo'),'月K');assert.equal(supportsPeriod('5m',{minute_periods:false}),false);assert.equal(supportsPeriod('1w',{minute_periods:false}),true)});\n""", encoding='utf-8')
p = root / 'tests/unit/test_v135_chart_controls.py'
p.write_text("""from pathlib import Path\nROOT=Path(__file__).resolve().parents[2]\ndef test_v135_chart_controls_contract():\n    app=(ROOT/'src/fund_platform/static/app.js').read_text(encoding='utf-8')\n    api=(ROOT/'src/fund_platform/static/api.js').read_text(encoding='utf-8')\n    html=(ROOT/'src/fund_platform/static/index.html').read_text(encoding='utf-8')\n    assert 'chartRequestId' in app and 'requestedPeriod!==state.period' in app\n    assert 'applyCapabilities(payload.capabilities' in app\n    assert "cache:'no-store'" in api\n    assert 'id="periodHint"' in html\n""", encoding='utf-8')

print('v1.3.5 patch applied')
