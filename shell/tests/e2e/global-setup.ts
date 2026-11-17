// Card 391 (B082): one id per Playwright run, for the phone renderer builds (tests/e2e/phone/server.ts).
//
// The build itself stays lazy: the first worker whose spec calls buildRenderer() takes out/b037/renderer.lock, builds,
// and marks the folder with this run's id; every other worker waits on the lock, sees the mark and reuses the build.
// So a run of only non-phone specs (card375 alone, say) builds nothing, and a phone run builds each folder once,
// whichever specs were picked on the command line (the setup is not told which files were selected).
// B037_SKIP_BUILD=1 still reuses any build that is there.
//
// Environment set here reaches every worker the runner starts afterwards.
export default function globalSetup() {
  process.env.B037_RUN_ID = `${process.pid}-${Date.now()}`;
}
