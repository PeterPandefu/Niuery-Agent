const assert = require('node:assert/strict');
const Module = require('node:module');

const handlers = new Map();
let createdWindow;
let lineHandler;

const app = {
  whenReady: () => Promise.resolve(),
  getPath: () => process.cwd(),
  on: () => {},
  quit: () => {},
};

class BrowserWindow {
  constructor() {
    createdWindow = this;
    this.webContents = {
      send: () => {},
      setBackgroundThrottling: () => {},
      setWindowOpenHandler: () => {},
      on: () => {},
    };
  }
  setMenuBarVisibility() {}
  loadFile() {}
  static getAllWindows() { return []; }
}

const worker = {
  exitCode: null,
  stdin: {
    writable: true,
    write: (line) => {
      const message = JSON.parse(line);
      lineHandler?.(JSON.stringify({ requestId: message.requestId, ok: true, payload: {} }));
    },
    end: () => {},
  },
  stdout: {},
  stderr: { on: () => {} },
  on: () => {},
  once: () => {},
  kill: () => {},
};

const originalLoad = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === 'electron') {
    return {
      app,
      BrowserWindow,
      ipcMain: { handle: (name, handler) => handlers.set(name, handler) },
      dialog: { showOpenDialog: async () => ({ canceled: true, filePaths: [] }) },
    };
  }
  if (request === 'node:child_process') return { spawn: () => worker };
  if (request === 'node:readline') return { createInterface: () => ({ on: (_, callback) => { lineHandler = callback; } }) };
  return originalLoad.call(this, request, parent, isMain);
};

(async () => {
  require('../electron/main.cjs');
  await new Promise((resolve) => setImmediate(resolve));
  const requestHandler = handlers.get('worker:request');
  assert.equal(typeof requestHandler, 'function');

  for (const method of ['mode.get', 'mode.set']) {
    await assert.doesNotReject(
      requestHandler({ sender: createdWindow.webContents }, method, {}),
      `${method} 不应被 IPC 白名单拒绝`,
    );
  }

  console.log('IPC 白名单回归检查通过。');
})().catch((error) => {
  console.error(`IPC 白名单回归检查失败：${error.message}`);
  process.exitCode = 1;
}).finally(() => {
  Module._load = originalLoad;
});
