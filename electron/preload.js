
const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('overlay', {
  onTelemetry: (cb) => {
    const handler = (_evt, payload) => cb(payload);
    ipcRenderer.on('telemetry', handler);
    return () => ipcRenderer.removeListener('telemetry', handler);
  },
  onZones: (cb) => {
    const handler = (_evt, zones) => cb(zones);
    ipcRenderer.on('zones', handler);
    return () => ipcRenderer.removeListener('zones', handler);
  },
  sendZones: (zones) => ipcRenderer.send('zones', zones),
  setLeadTime: (sec) => ipcRenderer.send('set-lead-time', sec),
  onSetLeadTime: (cb) => {
    const handler = (_evt, sec) => cb(sec);
    ipcRenderer.on('set-lead-time', handler);
    return () => ipcRenderer.removeListener('set-lead-time', handler);
  }
});
