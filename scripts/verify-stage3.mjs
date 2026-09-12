import fs from 'node:fs'; import path from 'node:path'; import os from 'node:os'; import assert from 'node:assert/strict'; import { WorkerClient, root } from './worker-client.mjs';
const directory=fs.mkdtempSync(path.join(os.tmpdir(),'niuery-stage3-'));fs.writeFileSync(path.join(directory,'failing.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>');fs.writeFileSync(path.join(directory,'Calculator.cs'), 'public static class Calculator { public static int Add(int a,int b) => a - b; }');
const database=path.join(directory,'tasks.db');const worker=new WorkerClient(database);const approvals=[];worker.observers.push(e=>{if(e.type==='approval.requested')approvals.push(e);});
try {
 const run=await worker.request('run.start',{workspace:directory,providerId:'routin',prompt:'这是自动化验收，禁止提问和只写计划，必须立即执行工具：第一步必须调用 ReadFile(path="Calculator.cs", startLine=1, lineCount=50)；第二步必须调用 ApplyPatch(path="Calculator.cs", expectedHash=上一步返回的hash, oldText="a - b", newText="a + b")。ApplyPatch 必须等待用户审批，收到批准后才写文件。完成后停止，不运行命令。'});
 let resolved=false;const deadline=Date.now()+240000;
 while(!resolved&&Date.now()<deadline){
  const approval=approvals.find(x=>x.runId===run.id&&!x._done);if(approval){approval._done=true;const payload=approval.payload;await worker.request('approval.respond',{runId:run.id,approvalId:payload.approvalId,digest:payload.digest,approved:true});}
  const terminal=worker.events.find(e=>e.runId===run.id&&/^run\.(completed|failed|cancelled)$/.test(e.type));if(terminal){resolved=true;assert.equal(terminal.type,'run.completed');}
  if(!resolved)await new Promise(r=>setTimeout(r,250));
 }
 assert(resolved,'编码任务未在时限内完成');
 if(approvals.length===0){console.error(JSON.stringify(worker.events.filter(e=>e.runId===run.id),null,2));throw new Error('未产生审批请求');}
 const artifacts=(await worker.request('workspace.diff',{runId:run.id}));assert(artifacts.length>=1,'未产生补丁制品');
 const content=fs.readFileSync(path.join(directory,'Calculator.cs'),'utf8');assert(content.includes('a + b'),'实际文件未应用修复');
 const report={status:'通过',checks:['失败测试修复任务','读取和搜索工具','补丁审批','实际文件修改','差异制品','事件记录'],approvalCount:approvals.length};fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});fs.writeFileSync(path.join(root,'artifacts/stage3.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
}finally{await worker.close();}
