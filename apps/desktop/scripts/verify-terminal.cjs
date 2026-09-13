const { app } = require('electron');
const pty = require('node-pty');

const marker = 'NIUERY_CONPTY_READY';
let output = '';
let sentCommand = false;

const timeout = setTimeout(() => finish(1, '终端验收超时。'), 15000);

function finish(code, message) {
  clearTimeout(timeout);
  process.stdout.write(`${message}\n`);
  app.exit(code);
}

app.whenReady().then(() => {
  let terminal;
  try {
    terminal = pty.spawn('powershell.exe', ['-NoLogo', '-NoProfile'], {
      name: 'xterm-256color',
      cols: 80,
      rows: 24,
      cwd: process.cwd(),
      env: process.env,
      useConpty: true,
    });
  } catch (error) {
    finish(1, `终端验收失败：${error.message}`);
    return;
  }

  terminal.onData(data => {
    output += data;
    if (!sentCommand && output.includes(marker)) {
      sentCommand = true;
      terminal.resize(101, 33);
      terminal.write("Write-Output ('NIUERY_CONPTY_SIZE=' + $Host.UI.RawUI.WindowSize.Width + 'x' + $Host.UI.RawUI.WindowSize.Height)\r");
      terminal.write('exit\r');
    }
  });
  terminal.onExit(({ exitCode }) => {
    const passed = exitCode === 0 && output.includes(marker) && output.includes('NIUERY_CONPTY_SIZE=101x33');
    finish(passed ? 0 : 1, passed ? 'ConPTY 终端验收通过。' : `终端验收失败：退出码 ${exitCode}，输出：${JSON.stringify(output)}`);
  });
  terminal.write(`Write-Output '${marker}'\r`);
});
