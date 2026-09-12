const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('agent', {
  request: (method, payload = {}) => ipcRenderer.invoke('worker:request', method, payload),
  chooseWorkspace: () => ipcRenderer.invoke('workspace:choose'),
  onEvent: callback => { const handler = (_, data) => callback(data); ipcRenderer.on('worker:event', handler); return () => ipcRenderer.removeListener('worker:event', handler); },
  onStatus: callback => { const handler = (_, data) => callback(data); ipcRenderer.on('worker:status', handler); return () => ipcRenderer.removeListener('worker:status', handler); }
});
