
// Electron + irsdk-node v4 bridge with countdown overlay
const { app, BrowserWindow, globalShortcut, ipcMain } = require('electron');
const path = require('path');

let mainWin;
let overlayWin;
let overlayClickThrough = true;

function createMainWindow() {
  mainWin = new BrowserWindow({
    width: 1280,
    height: 830,
    backgroundColor: '#0b1020',
    webPreferences: { preload: path.join(__dirname, 'preload.js') }
  });
  const isDev = !app.isPackaged;
  if (isDev) mainWin.loadURL('http://localhost:5173');
  else mainWin.loadFile(path.join(__dirname, '../dist/index.html'));
}

function createOverlayWindow() {
  overlayWin = new BrowserWindow({
    width: 520,
    height: 180,
    frame: false,
    transparent: true,
    resizable: true,
    skipTaskbar: true,
    alwaysOnTop: true,
    hasShadow: false,
    backgroundColor: '#00000000',
    webPreferences: { preload: path.join(__dirname, 'preload.js') }
  });
  const isDev = !app.isPackaged;
  if (isDev) overlayWin.loadURL('http://localhost:5173/overlay.html');
  else overlayWin.loadFile(path.join(__dirname, '../dist/overlay.html'));
  overlayWin.setIgnoreMouseEvents(overlayClickThrough, { forward: true });
}

function registerHotkeys() {
  globalShortcut.register('F9', () => {
    if (!overlayWin) return;
    if (overlayWin.isVisible()) overlayWin.hide();
    else overlayWin.show();
  });
  globalShortcut.register('F8', () => {
    overlayClickThrough = !overlayClickThrough;
    if (overlayWin && !overlayWin.isDestroyed()) {
      overlayWin.setIgnoreMouseEvents(overlayClickThrough, { forward: true });
    }
  });
}

function pipeToUI(payload) {
  if (mainWin && !mainWin.isDestroyed()) mainWin.webContents.send('telemetry', payload);
  if (overlayWin && !overlayWin.isDestroyed()) overlayWin.webContents.send('telemetry', payload);
}

function normalize(v = {}) {
  return {
    Throttle: Number((v.Throttle ?? 0) * 100),
    Brake: Number((v.Brake ?? 0) * 100),
    SteeringWheelAngle: Number(v.SteeringWheelAngle ?? 0),
    LapDistPct: Number((v.LapDistPct ?? 0) * 100),
  };
}

function startSdkBridge() {
  try {
    const irsdk = require('irsdk-node'); // v4
    const sdk = irsdk.create ? irsdk.create() : new irsdk.IRacingSDK?.();
    if (!sdk) throw new Error('irsdk-node v4 not initialized');

    if (typeof sdk.on === 'function') {
      sdk.on('telemetry', (t) => pipeToUI(normalize(t)));
      if (typeof sdk.start === 'function') sdk.start();
    } else if (typeof sdk.getTelemetry === 'function') {
      setInterval(() => {
        const t = sdk.getTelemetry();
        if (t) pipeToUI(normalize(t));
      }, 60);
    } else {
      throw new Error('Unknown irsdk-node v4 interface; no on()/getTelemetry()');
    }
  } catch (e) {
    console.error('No iRacing SDK available. Running without live telemetry.', e.message);
  }
}

app.whenReady().then(() => {
  createMainWindow();
  createOverlayWindow();
  registerHotkeys();
  startSdkBridge();

  ipcMain.on('zones', (_ev, zones) => {
    if (overlayWin && !overlayWin.isDestroyed()) overlayWin.webContents.send('zones', zones);
  });
  ipcMain.on('set-lead-time', (_ev, sec) => {
    if (overlayWin && !overlayWin.isDestroyed()) overlayWin.webContents.send('set-lead-time', sec);
  });

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createMainWindow();
      createOverlayWindow();
    }
  });
});

app.on('will-quit', () => globalShortcut.unregisterAll());
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
