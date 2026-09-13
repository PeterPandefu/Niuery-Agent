const { app, BrowserWindow, ipcMain, dialog } = require('electron');
const { shell } = require('electron');
const { spawn, spawnSync } = require('node:child_process');
const { createInterface } = require('node:readline');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../../..');
let window, worker, stopping = false;
let selectedWorkspace = null;
const pending = new Map();
const terminals = new Map();
function checkGitInstallation() {
  const result = spawnSync('git', ['--version'], { windowsHide: true, stdio: 'ignore' });
  if (result.error || result.status !== 0) {
    dialog.showMessageBoxSync({
      type: 'warning',
      title: '未检测到 Git',
      message: '未检测到 Git 命令。',
      detail: '项目编码依赖 Git 生成文件差异；未安装 Git 可能导致编码模式异常。请安装 Git 后重启应用。',
    });
  }
}
function request(method, payload = {}) {
  return new Promise((resolve, reject) => {
    if (!worker || worker.exitCode !== null || !worker.stdin.writable) return reject(new Error('执行器未连接，请重启应用。'));
    const requestId = crypto.randomUUID();
    const timeout = setTimeout(() => { pending.delete(requestId); reject(new Error('执行器响应超时。')); }, 15000);
    pending.set(requestId, { resolve, reject, timeout });
    worker.stdin.write(JSON.stringify({ version: 1, requestId, method, payload }) + '\n');
  });
}
function startWorker() {
  worker = spawn('dotnet', [path.join(root, 'src/Niuery.Agent.Worker/bin/Debug/net10.0/Niuery.Agent.Worker.dll'), path.join(root, 'config/providers.local.json'), path.join(app.getPath('userData'), 'tasks.db')], { cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  createInterface({ input: worker.stdout }).on('line', line => {
    try {
      const message = JSON.parse(line);
      if (message.requestId && pending.has(message.requestId)) {
        const entry = pending.get(message.requestId); clearTimeout(entry.timeout); pending.delete(message.requestId);
        message.ok ? entry.resolve(message.payload) : entry.reject(new Error(message.error.message));
      } else if (message.eventData) window?.webContents.send('worker:event', message.eventData);
    } catch { window?.webContents.send('worker:status', '执行器返回了无效消息。'); }
  });
  worker.stderr.on('data', () => window?.webContents.send('worker:status', '执行器报告错误，请检查本地配置。'));
  const disconnected = () => {
    for (const entry of pending.values()) { clearTimeout(entry.timeout); entry.reject(new Error('执行器连接已断开。')); }
    pending.clear(); if (!stopping) window?.webContents.send('worker:status', '执行器已退出，任务将在重启后标记为中断。');
  };
  worker.on('exit', disconnected); worker.on('error', disconnected);
}
app.whenReady().then(() => {
  if (process.env.NIUERY_TEST_DATA) app.setPath('userData', process.env.NIUERY_TEST_DATA);
  checkGitInstallation();
  window = new BrowserWindow({ width: 1440, height: 940, minWidth: 940, minHeight: 660, title: 'Niuery Agent', backgroundColor: '#f5f7fa', webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true } });
  if (process.env.NIUERY_TEST_DATA) window.webContents.setBackgroundThrottling(false);
  window.setMenuBarVisibility(false);
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', e => e.preventDefault());
  ipcMain.handle('worker:request', async (event, method, payload) => {
    if (event.sender !== window.webContents || !['hello','project.open','providers.list','providers.save','providers.test','providers.sync','history.list','events.list','run.start','chat.start','task.delete','run.cancel','run.continue','mode.get','mode.set','approval.respond','workspace.diff','workspace.gitDiff','workspace.tree','workspace.read','workspace.apply','workspace.command','artifact.undo','worker.shutdown'].includes(method)) throw new Error('请求不被允许。');
    if (JSON.stringify(payload).length > 1000000) throw new Error('请求过大。');
    return request(method, payload);
  });
  ipcMain.handle('workspace:choose', async event => {
    if (event.sender !== window.webContents) throw new Error('窗口不被允许。');
    const result = await dialog.showOpenDialog(window, { title: '选择项目目录', properties: ['openDirectory'] });
    if (result.canceled) return null;
    selectedWorkspace = path.resolve(result.filePaths[0]);
    return selectedWorkspace;
  });
  ipcMain.handle('workspace:open', async (event, target) => { if (event.sender !== window.webContents) throw new Error('窗口不被允许。'); await shell.openPath(target); });
  ipcMain.handle('terminal:start', async (event, cwd) => {
    if (event.sender !== window.webContents || typeof cwd !== 'string' || !path.isAbsolute(cwd)) throw new Error('终端目录无效。');
    const resolvedCwd = path.resolve(cwd);
    if (!selectedWorkspace || (resolvedCwd !== selectedWorkspace && !resolvedCwd.startsWith(selectedWorkspace + path.sep))) throw new Error('终端只能在当前工作区内启动。');
    if (!require('node:fs').existsSync(resolvedCwd)) throw new Error('终端目录不存在。');
    const id = crypto.randomUUID();
    const child = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass'], { cwd: resolvedCwd, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    terminals.set(id, child);
    const send = (type, data) => window?.webContents.send(`terminal:${type}`, { id, data });
    child.stdout.on('data', data => send('data', data.toString()));
    child.stderr.on('data', data => send('data', data.toString()));
    child.on('exit', code => { send('exit', String(code ?? 0)); terminals.delete(id); });
    return { id };
  });
  if (typeof ipcMain.on === 'function') {
    ipcMain.on('terminal:write', (event, id, data) => { if (event.sender === window.webContents && terminals.get(id)?.stdin.writable && typeof data === 'string') terminals.get(id).stdin.write(data); });
    ipcMain.on('terminal:close', (event, id) => { if (event.sender === window.webContents) { const child = terminals.get(id); if (child) { child.kill(); terminals.delete(id); } } });
  }
  startWorker(); window.loadFile(path.join(__dirname, '../dist/index.html'));
});
app.on('before-quit', event => {
  if (stopping || !worker || worker.exitCode !== null) return;
  event.preventDefault(); stopping = true;
  request('worker.shutdown').catch(() => {}).finally(() => worker.stdin.end());
  worker.once('exit', () => app.quit());
  setTimeout(() => { worker.kill(); app.quit(); }, 5000);
});
app.on('window-all-closed', () => app.quit());
