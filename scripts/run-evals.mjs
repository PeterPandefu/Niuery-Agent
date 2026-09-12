import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { WorkerClient, root } from './worker-client.mjs';

const tasks = JSON.parse(fs.readFileSync(path.resolve('evals/tasks.json'), 'utf8'));
const reportPath = path.join(root, 'artifacts/evals-baseline.json');
const results = [];
fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });

async function runTask(task) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'niuery-eval-'));
  const worker = new WorkerClient(path.join(directory, 'tasks.db'));
  const started = Date.now();
  try {
    const run = await worker.request('run.start', {
      workspace: directory,
      providerId: 'routin-mini',
      prompt: `评测任务 ${task.id}：${task.prompt}。这是基线协议评测，不需要修改文件和调用工具。请只用中文回复“评测通过”，不要提问。`
    });
    const startedEvent = await worker.waitFor(e => e.runId === run.id && e.type === 'run.started', 30000);
    const firstOutputPromise = worker.waitFor(e => e.runId === run.id && e.type === 'message.delta', 30000).catch(() => null);
    const terminal = await worker.waitFor(e => e.runId === run.id && /^run\.(completed|failed|cancelled)$/.test(e.type), 90000);
    const firstOutput = await firstOutputPromise;
    const output = worker.events.filter(e => e.runId === run.id && e.type === 'message.delta').map(e => e.payload.text).join('');
    return { id: task.id, category: task.category, status: terminal.type === 'run.completed' && output.includes('评测通过') ? 'passed' : 'failed', terminal: terminal.type, elapsedMilliseconds: Date.now() - started, timeToStartedMilliseconds: Date.parse(startedEvent.timestamp)-started, timeToFirstOutputMilliseconds:firstOutput?Date.parse(firstOutput.timestamp)-started:null, eventCount:worker.events.filter(e=>e.runId===run.id).length, outputChars: output.length };
  } catch (error) {
    return { id: task.id, category: task.category, status: 'failed', error: String(error.message), elapsedMilliseconds: Date.now() - started };
  } finally { await worker.close(); }
}

for (let i = 0; i < tasks.length; i += 5) {
  results.push(...await Promise.all(tasks.slice(i, i + 5).map(runTask)));
  const passed = results.filter(r => r.status === 'passed').length;
  fs.writeFileSync(reportPath, JSON.stringify({ date: new Date().toISOString(), provider: 'routin-mini', model: 'gpt-4o-mini', taskCount: tasks.length, completed: results.length, passed, failed: results.length - passed, successRate: results.length ? passed / results.length : 0, results, scope: '基线协议与任务完成率；不等同于逐条真实代码修改质量评测' }, null, 2));
  console.log(`已完成 ${Math.min(i + 5, tasks.length)}/${tasks.length} 条评测`);
}
const passed = results.filter(r => r.status === 'passed').length;
console.log(JSON.stringify({ taskCount: tasks.length, passed, failed: tasks.length - passed, successRate: passed / tasks.length }));
