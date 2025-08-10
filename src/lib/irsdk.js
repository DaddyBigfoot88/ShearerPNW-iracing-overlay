// src/lib/irsdk.js
let real;
try {
  // Attempt to load the native module if it exists locally
  real = await import('irsdk-node');
} catch {
  real = null;
}

export function getTelemetry() {
  if (real && real.default) {
    // Replace with real API usage if/when you wire it in
    return real.default;
  }
  // Mocked data so UI runs in CI / on machines without SDK
  return {
    isMock: true,
    speed: 142,
    gear: 4,
    rpm: 7100,
    lapTime: 92.4
  };
}
