import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { WorkerClient, root } from './worker-client.mjs';
const directory=fs.mkdtempSync(path.join(os.tmpdir(),'niuery-stage2-'));fs.mkdirSync(path.join(directory,'.git'));const database=path.join(directory,'tasks.db');
let worker=new WorkerClient(database);
try{
 assert.equal((await worker.request('hello')).version,1);
 const providers=await worker.request('providers.list');assert(providers.some(p=>p.id==='routin'&&p.model==='gpt-4.1'));
 const run=await worker.request('run.start',{workspace:directory,providerId:'routin',prompt:'请只用中文回复：桌面通信验收成功。不要调用工具。'});
 const completed=await worker.waitFor(e=>e.runId===run.id&&/^run\.(completed|failed|cancelled)$/.test(e.type));assert.equal(completed.type,'run.completed');
 assert(worker.events.some(e=>e.runId===run.id&&e.type==='message.delta'));
 const cancel=await worker.request('run.start',{workspace:directory,providerId:'routin',prompt:'请用中文详细说明软件架构。'});
 await worker.request('run.cancel',{runId:cancel.id});assert.equal((await worker.waitFor(e=>e.runId===cancel.id&&e.type==='run.cancelled')).type,'run.cancelled');
 const crash=await worker.request('run.start',{workspace:directory,providerId:'routin',prompt:'请详细解释编译器实现。'});
 await worker.waitFor(e=>e.runId===crash.id&&e.type==='run.started');worker.process.kill();await worker.exit;
 worker=new WorkerClient(database);await worker.request('hello');
 const history=await worker.request('history.list');assert.equal(history.find(r=>r.id===run.id).status,'completed');assert.equal(history.find(r=>r.id===crash.id).status,'interrupted');
 const events=await worker.request('events.list',{runId:run.id});assert(events.length>1);assert(events.every((e,i)=>e.sequence===i+1));
 const report={date:new Date().toISOString(),model:'gpt-4.1',checks:['协议握手','真实模型流式输出','任务取消','进程退出后中断标记','SQLite 历史恢复','事件顺序'],status:'通过'};
 fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});fs.writeFileSync(path.join(root,'artifacts/stage2.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
}finally{await worker.close();}
