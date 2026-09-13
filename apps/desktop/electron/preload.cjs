const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('agent', {
  request: (method, payload = {}) => ipcRenderer.invoke('worker:request', method, payload),
  chooseWorkspace: () => ipcRenderer.invoke('workspace:choose'),
  setWorkspace: workspace => ipcRenderer.invoke('workspace:set', workspace),
  openInExplorer: path => ipcRenderer.invoke('workspace:open', path),
  startTerminal: cwd => ipcRenderer.invoke('terminal:start', cwd),
  writeTerminal: (id, data) => ipcRenderer.send('terminal:write', id, data),
  resizeTerminal: (id, cols, rows) => ipcRenderer.send('terminal:resize', id, cols, rows),
  closeTerminal: id => ipcRenderer.send('terminal:close', id),
  onTerminalData: callback => { const handler = (_, data) => callback(data); ipcRenderer.on('terminal:data', handler); return () => ipcRenderer.removeListener('terminal:data', handler); },
  onTerminalExit: callback => { const handler = (_, data) => callback(data); ipcRenderer.on('terminal:exit', handler); return () => ipcRenderer.removeListener('terminal:exit', handler); },
  onEvent: callback => { const handler = (_, data) => callback(data); ipcRenderer.on('worker:event', handler); return () => ipcRenderer.removeListener('worker:event', handler); },
  onStatus: callback => { const handler = (_, data) => callback(data); ipcRenderer.on('worker:status', handler); return () => ipcRenderer.removeListener('worker:status', handler); }
});
