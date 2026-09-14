export const flush = async () => { for (let index = 0; index < 40; index++) await Promise.resolve(); };

export class Clock {
  now = 1000000; id = 0; timers = new Map();
  setTimeout = (fn, ms) => { const id = ++this.id; this.timers.set(id, { at: this.now + ms, fn }); return id; };
  clearTimeout = id => this.timers.delete(id);
  async advance(ms) {
    const end = this.now + ms;
    for (;;) {
      const next = [...this.timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!next) break;
      this.now = next[1].at; this.timers.delete(next[0]); next[1].fn(); await flush();
    }
    this.now = end; await flush();
  }
}
