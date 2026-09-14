import assert from 'node:assert/strict';

export class Store {
  records = []; locks = new Map(); failState = null;
  async list() { return structuredClone(this.records); }
  async add(record) {
    if (this.failState === 'add') throw new Error('disk full');
    const prior = this.records.find(item => item.serviceId === record.serviceId && item.idempotencyKey === record.idempotencyKey);
    if (prior) return structuredClone(prior);
    const saved = { ...structuredClone(record), localId: this.records.length + 1, version: 1 };
    this.records.push(saved); return structuredClone(saved);
  }
  async update(previous, next) {
    if (this.failState === next.state) throw new Error('disk full');
    const at = this.records.findIndex(record => record.localId === previous.localId);
    assert.equal(this.records[at].version, previous.version);
    this.records[at] = structuredClone({ ...next, version: previous.version + 1 });
    return structuredClone(this.records[at]);
  }
  async withQueue(service, printer, work) {
    const key = JSON.stringify([service, printer]);
    const pending = (this.locks.get(key) || Promise.resolve()).then(work);
    this.locks.set(key, pending.catch(() => {})); return pending;
  }
}
