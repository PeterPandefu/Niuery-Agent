import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
export const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
export class WorkerClient {
  constructor(database){
    this.events=[];this.pending=new Map();this.observers=[];
    this.process=spawn('dotnet',[path.join(root,'src/Niuery.Agent.Worker/bin/Debug/net10.0/Niuery.Agent.Worker.dll'),path.join(root,'config/providers.local.json'),database],{cwd:root,windowsHide:true,stdio:['pipe','pipe','pipe']});
    this.exit=new Promise(resolve=>this.process.once('exit',resolve));
    this.process.once('exit',()=>{for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(new Error('执行器已退出。'));}this.pending.clear();});
    createInterface({input:this.process.stdout}).on('line',line=>{
      const m=JSON.parse(line);if(m.eventData){this.events.push(m.eventData);for(const observer of this.observers)observer(m.eventData);}
      if(m.requestId&&this.pending.has(m.requestId)){const p=this.pending.get(m.requestId);clearTimeout(p.timer);this.pending.delete(m.requestId);m.ok?p.resolve(m.payload):p.reject(new Error(m.error.message));}
    });
    this.process.stderr.on('data',()=>{});
  }
  request(method,payload={},timeoutMs=15000){return new Promise((resolve,reject)=>{const requestId=randomUUID();const timer=setTimeout(()=>{this.pending.delete(requestId);reject(new Error('请求超时。'));},timeoutMs);this.pending.set(requestId,{resolve,reject,timer});this.process.stdin.write(JSON.stringify({version:1,requestId,method,payload})+'\n');});}
  waitFor(predicate,timeout=180000){const existing=this.events.find(predicate);if(existing)return Promise.resolve(existing);return new Promise((resolve,reject)=>{const observer=e=>{if(predicate(e)){clearTimeout(timer);this.observers=this.observers.filter(x=>x!==observer);resolve(e);}};const timer=setTimeout(()=>{this.observers=this.observers.filter(x=>x!==observer);reject(new Error('等待执行事件超时。'));},timeout);this.observers.push(observer);});}
  async close(){if(this.process.exitCode===null){await this.request('worker.shutdown').catch(()=>{});this.process.stdin.end();await this.exit;}}
}
