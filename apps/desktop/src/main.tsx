import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { FolderOpen, Plus, ArrowUp, Square, Settings2, TerminalSquare, FileDiff, CheckCircle2 } from 'lucide-react';
import './style.css';
import type { Method } from './protocol.generated';

type Run = { id: string; taskId: string; workspace: string; prompt: string; provider: string; status: string; created: string };
type Event = { runId: string; sequence: number; type: string; timestamp: string; payload: Record<string, any> };
type Provider = { id: string; model: string; kind: string };
declare global { interface Window { agent: { request: (method: Method, payload?: object) => Promise<any>; chooseWorkspace: () => Promise<string | null>; onEvent: (cb: (e: Event) => void) => () => void; onStatus: (cb: (s: string) => void) => () => void } } }
const statuses: Record<string,string> = { running:'执行中', completed:'已完成', failed:'失败', cancelled:'已取消', interrupted:'已中断' };
function App() {
  const [runs,setRuns] = useState<Run[]>([]), [providers,setProviders] = useState<Provider[]>([]);
  const [selected,setSelected] = useState<string | null>(null), [events,setEvents] = useState<Event[]>([]);
  const [workspace,setWorkspace] = useState(localStorage.getItem('workspace') || ''), [provider,setProvider] = useState('');
  const [prompt,setPrompt] = useState(''), [error,setError] = useState(''), [busy,setBusy] = useState(false);
  const [tab,setTab] = useState('changes');
  const refresh = () => window.agent.request('history.list').then(setRuns).catch(e=>setError(e.message));
  useEffect(()=>{
    window.agent.request('hello').then(()=>window.agent.request('providers.list')).then((p:Provider[])=>{setProviders(p);setProvider(p[0]?.id || '');refresh();}).catch(e=>setError(e.message));
    return window.agent.onStatus(setError);
  },[]);
  useEffect(()=>{
    if (!selected) { setEvents([]); return; }
    let alive=true;
    setEvents([]);
    window.agent.request('events.list',{runId:selected}).then((items:Event[])=>{if(alive)setEvents(old=>Array.from(new Map([...items,...old.filter(x=>x.runId===selected)].map(x=>[x.sequence,x])).values()).sort((a,b)=>a.sequence-b.sequence));}).catch(e=>setError(e.message));
    const off=window.agent.onEvent(e=>{if(e.runId===selected)setEvents(old=>old.some(x=>x.sequence===e.sequence)?old:[...old,e].sort((a,b)=>a.sequence-b.sequence));if(e.type.startsWith('run.'))refresh();});
    return()=>{alive=false;off();};
  },[selected]);
  const current=runs.find(r=>r.id===selected), running=runs.some(r=>r.status==='running');
  async function choose(){const value=await window.agent.chooseWorkspace();if(value){setWorkspace(value);localStorage.setItem('workspace',value);}}
  async function start(){setBusy(true);setError('');try{const run=await window.agent.request('run.start',{workspace,providerId:provider,prompt});setSelected(run.id);setPrompt('');await refresh();}catch(e){setError((e as Error).message);}finally{setBusy(false);}}
  return <div className="app">
    <aside className="sidebar"><div className="brand"><span className="brand-mark">N</span><strong>Niuery <span>Agent</span></strong></div>
      <button className="new-task" onClick={()=>setSelected(null)}><Plus size={17}/>新建任务</button>
      <div className="section-label">本地任务 <span>{runs.length}</span></div>
      <nav>{runs.map(r=><button key={r.id} className={`task ${r.id===selected?'active':''}`} onClick={()=>setSelected(r.id)}><span className={`dot ${r.status}`}/><span>{r.prompt}<small>{statuses[r.status]} · {new Date(r.created).toLocaleDateString('zh-CN')}</small></span></button>)}</nav>
      <div className="sidebar-bottom"><span className="online"/>本地执行 · 数据保存在本机</div>
    </aside>
    <main><header><div><span className="eyebrow">项目工作台</span><h1>{current?'任务详情':'开始一个新任务'}</h1></div><button className="workspace" onClick={choose} title={workspace}><FolderOpen size={17}/>{workspace.split(/[\\/]/).pop()||'选择项目'}</button></header>
      {error&&<div role="alert" className="error">{error}<button onClick={()=>setError('')}>关闭</button></div>}
      <section className="conversation">
        {!current?<div className="welcome"><div className="welcome-icon"><TerminalSquare size={32}/></div><h2>把下一步，交给 Agent。</h2><p>打开一个项目，描述你想完成的工作。<br/>执行过程、代码修改和需要你决定的操作，都在这里。</p><div className="suggestions">{['解释这个项目的结构','找到并修复失败的测试','为一个函数补充测试'].map(s=><button key={s} onClick={()=>setPrompt(s)}>{s}</button>)}</div></div>:<><div className="user-message">{current.prompt}</div><div className="run-label"><span className={`dot ${current.status}`}/>{statuses[current.status]}<span>{providers.find(p=>p.id===current.provider)?.model}</span></div>
        <div className="answer">{events.filter(e=>e.type==='message.delta').map(e=>e.payload.text).join('')||'等待执行输出…'}</div>
        {events.filter(e=>e.type!=='message.delta').map(e=><div className="event" key={e.sequence}><CheckCircle2 size={14}/><span>{e.payload.message||e.type}</span></div>)}</>}
      </section>
      <footer><div className="composer"><textarea aria-label="任务说明" value={prompt} onChange={e=>setPrompt(e.target.value)} placeholder="描述任务、问题或希望实现的改动…" onKeyDown={e=>{if(e.key==='Enter'&&(e.ctrlKey||e.metaKey)&&prompt.trim()&&workspace&&!running)start();}}/><div className="composer-bottom"><label><Settings2 size={15}/><select aria-label="模型" value={provider} onChange={e=>setProvider(e.target.value)}>{providers.map(p=><option key={p.id} value={p.id}>{p.model}</option>)}</select></label>{running?<button aria-label="停止执行" className="send" onClick={()=>window.agent.request('run.cancel',{runId:runs.find(r=>r.status==='running')?.id}).catch(e=>setError(e.message))}><Square size={15}/></button>:<button aria-label="开始执行" className="send" disabled={busy||!workspace||!prompt.trim()||!provider} onClick={start}><ArrowUp size={19}/></button>}</div></div><div className="hint">修改和命令执行需要审批 · Ctrl + Enter 发送</div></footer>
    </main>
    <aside className="inspector"><div className="tabs"><button className={tab==='changes'?'chosen':''} onClick={()=>setTab('changes')}><FileDiff size={16}/>修改</button><button className={tab==='activity'?'chosen':''} onClick={()=>setTab('activity')}><TerminalSquare size={16}/>活动</button></div>{tab==='changes'?<div className="empty"><FileDiff size={30}/><h3>修改将在这里呈现</h3><p>执行后查看文件差异，<br/>再决定如何处理。</p></div>:<div className="activity">{events.map(e=><p key={e.sequence}><small>{new Date(e.timestamp).toLocaleTimeString('zh-CN')}</small>{e.payload.message||e.type}</p>)}</div>}<div className="inspector-note">工作区<br/><span>{current?.workspace||workspace||'尚未选择项目'}</span></div></aside>
  </div>;
}
createRoot(document.getElementById('root')!).render(<App/>);
