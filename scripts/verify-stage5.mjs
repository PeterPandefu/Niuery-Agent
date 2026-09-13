import fs from 'node:fs'; import path from 'node:path'; import os from 'node:os'; import assert from 'node:assert/strict'; import { WorkerClient, root } from './worker-client.mjs';
const directory=fs.mkdtempSync(path.join(os.tmpdir(),'niuery-stage5-'));const results=[];
fs.mkdirSync(path.join(directory,'.git'));for(const providerId of ['routin','routin-mini']){
 const worker=new WorkerClient(path.join(directory,providerId+'.db'));const started=Date.now();
 try{const run=await worker.request('run.start',{workspace:directory,providerId,prompt:'请只用中文回复“评测通过”，不要调用工具。'});const terminal=await worker.waitFor(e=>e.runId===run.id&&e.type==='run.completed',180000);const text=worker.events.filter(e=>e.runId===run.id&&e.type==='message.delta').map(e=>e.payload.text).join('');assert(text.includes('评测通过'));results.push({providerId,status:'通过',elapsedMilliseconds:Date.now()-started,outputChars:text.length,terminal:terminal.type});}
 finally{await worker.close();}
}
const report={date:new Date().toISOString(),results,ollama:{status:'按用户要求跳过'},status:results.length===2?'通过':'失败'};fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});fs.writeFileSync(path.join(root,'artifacts/stage5.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
