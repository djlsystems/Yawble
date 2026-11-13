import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron';
import type { CoreEvent, YawbleApi } from '../shared/api';

// Channel names are repeated here, not imported: a sandboxed preload can require only 'electron'.
const api: YawbleApi = {
  getInfo: () => ipcRenderer.invoke('core:getInfo'),
  getAvailableBackends: () => ipcRenderer.invoke('sandbox:getAvailableBackends'),
  // Plain JSON only: a Vue reactive proxy cannot be structured-cloned, and the core takes JSON anyway.
  call: (method, params) => ipcRenderer.invoke('core:call', method, params ? JSON.parse(JSON.stringify(params)) : null),
  onEvent: (listener) => {
    const handler = (_e: IpcRendererEvent, event: CoreEvent) => listener(event);
    ipcRenderer.on('core:event', handler);
    return () => ipcRenderer.off('core:event', handler);
  },
  openTerminal: (memberId) => ipcRenderer.invoke('window:openTerminal', memberId),
  pickFolder: () => ipcRenderer.invoke('dialog:pickFolder'),
  platform: process.platform,
  dimTitleBar: (dim) => ipcRenderer.send('window:dimTitleBar', dim),
  shellSettings: () => ipcRenderer.invoke('shell:settings'),
  setShellSettings: (patch) => ipcRenderer.invoke('shell:setSettings', patch ? JSON.parse(JSON.stringify(patch)) : {}),
  openDocuments: (teamId) => ipcRenderer.invoke('window:openDocuments', teamId),
  windowCommand: (command) => ipcRenderer.send('window:command', command),
  installUpdate: () => ipcRenderer.invoke('app:installUpdate'),
};
contextBridge.exposeInMainWorld('yawble', api);
