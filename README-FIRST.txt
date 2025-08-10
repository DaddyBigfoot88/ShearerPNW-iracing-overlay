
iRacing Overlay — Complete Build (irsdk-node v4)

Hotkeys:
- F9 = show/hide transparent overlay
- F8 = toggle click-through (unlock to drag/resize, lock to prevent mouse clicks)

Quick start (dev):
1) Install Node.js LTS (18 or 20 recommended; 22 works but may build native deps).
2) In PowerShell (no admin needed):
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
3) In this folder:
   npm install
   npm run dev
4) Load public/sample_reference.csv as your preferred lap. Click "Simulate Live" if you don't have live data yet.
5) Set "Overlay lead time (s)" to configure 3-2-1 warnings before brake zones.

Build a portable EXE (to send to Dad):
   npm run build:win
The .exe appears in the "release" folder.
