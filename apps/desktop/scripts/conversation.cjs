const assert = require('node:assert/strict');
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
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      let runs = Array.from({ length: 12 }, (_, index) => ({
        id: `chat-${index}`, taskId: 'chat-history', kind: 'chat', workspace: '',
        prompt: `第 ${index + 1} 轮问题：保留完整对话历史`, provider: 'test', status: 'completed',
        created: new Date(Date.UTC(2026, 8, 13, 1, index)).toISOString(),
      })).reverse();
      window.agent = {
        request: async (method, payload = {}) => {
          if (method === 'hello') return {};
          if (method === 'providers.list') return [{ id: 'test', model: 'test', kind: 'ollama' }];
          if (method === 'history.list') return [...runs];
          if (method === 'workspace.diff') return [];
          if (method === 'events.list') return [
            { runId: payload.runId, sequence: 1, type: 'run.started', timestamp: runs.find(run => run.id === payload.runId).created, payload: { message: '执行已开始。' } },
            { runId: payload.runId, sequence: 2, type: 'message.delta', timestamp: runs.find(run => run.id === payload.runId).created, payload: { text: Array.from({ length: 12 }, (_, line) => `第 ${line + 1} 行回复：这是一段用于验证历史滚动、问答定位与输入区位置的内容。`).join('\n') } },
            { runId: payload.runId, sequence: 3, type: 'run.completed', timestamp: runs.find(run => run.id === payload.runId).created, payload: { message: '执行完成。' } },
          ];
          if (method === 'run.continue') {
            const run = {
              id: `chat-${runs.length}`, taskId: 'chat-history', kind: 'chat', workspace: '',
              prompt: payload.prompt, provider: 'test', status: 'completed',
              created: new Date(Date.UTC(2026, 8, 13, 2, runs.length)).toISOString(),
            };
            runs = [...runs, run];
            return run;
          }
          throw new Error(`测试没有实现请求：${method}`);
        },
        chooseWorkspace: async () => null,
        onEvent: () => () => {},
        onStatus: () => () => {},
      };
    });
    await page.goto(server.resolvedUrls.local[0]);
    await page.getByRole('navigation', { name: 'Chat 任务列表' }).getByRole('button').first().click();
    await expect(page.locator('.answer')).toHaveCount(12);
    await expect(page.locator('.conversation-turn')).toHaveCount(12);
    await expect(page.locator('.turn-timeline .timeline-item')).toHaveCount(24);
    const scroll = page.locator('.conversation');
    const metrics = await scroll.evaluate(element => ({ height: element.clientHeight, content: element.scrollHeight, viewport: innerHeight }));
    console.log('对话滚动测量：' + JSON.stringify(metrics));
    assert(metrics.height < metrics.viewport && metrics.content > metrics.height, '长对话必须在视口内形成可滚动区域');
    await expect(page.locator('.keyframe')).toHaveCount(12);
    assert.equal(await page.locator('.keyframe-line').count(), 0, '关键帧轴应只显示短横线');
    const axis = await page.locator('.keyframe-axis').boundingBox();
    await page.mouse.move(axis.x + axis.width / 2, axis.y + 14);
    await expect.poll(async () => page.locator('.keyframe-bar').evaluateAll(elements => {
      const widths = elements.map(element => Number.parseFloat(getComputedStyle(element).width));
      return Math.max(...widths) - Math.min(...widths);
    })).toBeGreaterThan(5);
    await scroll.evaluate(element => { element.scrollTop = 300; });
    await expect.poll(() => scroll.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
    await page.getByRole('button', { name: '跳转到第 1 轮对话' }).click();
    await expect.poll(() => scroll.evaluate(element => element.scrollTop)).toBeLessThan(100);
    await scroll.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await page.getByLabel('任务说明').fill('发送后保持在底部');
    await page.getByRole('button', { name: '继续对话' }).click();
    await expect(page.locator('.conversation-turn')).toHaveCount(13);
    await expect.poll(async () => scroll.evaluate(element => element.scrollHeight - element.scrollTop - element.clientHeight)).toBeLessThan(20);
    const composer = await page.locator('.composer-area').boundingBox();
    assert(composer.y + composer.height <= 901, '滚动历史时输入区必须留在视口内');
    assert.equal(errors.length, 0, errors.join('\n'));
    console.log('对话滚动验收通过。');
  } finally {
    await browser?.close();
    await server.close();
  }
})().catch(error => { console.error('对话验收失败：' + error.message); process.exitCode = 1; });
