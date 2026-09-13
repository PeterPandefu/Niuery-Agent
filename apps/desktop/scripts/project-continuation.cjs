const path = require('node:path');
const { chromium, expect } = require('playwright/test');

(async () => {
  const { createServer } = await import('vite');
  const server = await createServer({ root: path.resolve(__dirname, '..'), logLevel: 'error', server: { host: '127.0.0.1', port: 0 } });
  await server.listen();
  let browser;
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' });
    const workspace = 'C:\\repo';
    const runs = [{ id: 'project-run-1', taskId: 'project-task', kind: 'project', workspace, prompt: '首轮任务', provider: 'test', status: 'completed', created: '2026-09-13T01:00:00.000Z' }];
    await page.addInitScript(({ workspace }) => {
      localStorage.setItem('workspace', workspace);
      localStorage.setItem('projects', JSON.stringify([{ id: workspace, path: workspace, name: 'repo' }]));
      localStorage.setItem('activeProjectId', workspace);
    }, { workspace });
    await page.addInitScript(({ workspace }) => {
      let runs = [{ id: 'project-run-1', taskId: 'project-task', kind: 'project', workspace, prompt: '首轮任务', provider: 'test', status: 'completed', created: '2026-09-13T01:00:00.000Z' }];
      window.agent = {
        request: async (method, payload = {}) => {
          if (method === 'hello') return {};
          if (method === 'providers.list') return [{ id: 'test', model: 'test', models: ['test'], kind: 'ollama' }];
          if (method === 'history.list') return [...runs];
          if (method === 'workspace.diff') return [];
          if (method === 'events.list') return [
            { runId: payload.runId, sequence: 1, type: 'run.started', timestamp: runs[0].created, payload: { message: '执行已开始。' } },
            { runId: payload.runId, sequence: 2, type: 'message.delta', timestamp: runs[0].created, payload: { text: '首轮完成。' } },
            { runId: payload.runId, sequence: 3, type: 'run.completed', timestamp: runs[0].created, payload: { message: '执行完成。' } },
          ];
          if (method === 'run.continue') {
            window.__continuationRequested = true;
            const next = { ...runs[0], id: 'project-run-2', parentId: runs[0].id, prompt: payload.prompt, created: '2026-09-13T01:01:00.000Z' };
            runs = [...runs, next];
            return next;
          }
          throw new Error(`测试没有实现请求：${method}`);
        },
        chooseWorkspace: async () => null,
        onEvent: () => () => {},
        onStatus: () => () => {},
      };
    }, { workspace });
    await page.goto(server.resolvedUrls.local[0]);
    await page.getByRole('button', { name: /首轮任务/ }).click();
    await page.getByLabel('任务说明').fill('第二轮任务');
    const send = page.getByRole('button', { name: '继续执行' });
    await expect(send).toBeEnabled();
    await send.click();
    await expect.poll(() => page.evaluate(() => window.__continuationRequested)).toBe(true);
    await expect(page.getByLabel('任务说明')).toHaveValue('');
    console.log('项目任务完成后可发送第二轮验收通过。');
  } finally {
    await browser?.close();
    await server.close();
  }
})().catch(error => { console.error('项目任务第二轮验收失败：' + error.message); process.exitCode = 1; });
