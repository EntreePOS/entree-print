import { mkdir, realpath } from 'node:fs/promises';
import { isAbsolute, join } from 'node:path';
import { createHash } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

const failure = (code, message) => Object.assign(new Error(message), { code, delivery: 'not_sent' });
const busy = error => error?.code === 'ERR_SQLITE_ERROR' && [5, 6].includes(error.errcode & 255);

// Use an application-owned directory on a local disk. SQLite's OS locks must be
// shared by every dispatcher; copying this directory creates a different outbox.
export async function createSqliteOutboxStorage({ directory, lockTimeoutMs = 10000 } = {}) {
  if (typeof directory !== 'string' || !isAbsolute(directory) || directory.startsWith('\\\\') || directory.startsWith('//'))
    throw failure('OUTBOX_UNAVAILABLE', 'Choose an absolute local directory for the print outbox.');
  if (!Number.isInteger(lockTimeoutMs) || lockTimeoutMs < 1 || lockTimeoutMs > 120000)
    throw failure('OUTBOX_UNAVAILABLE', 'lockTimeoutMs must be between 1 and 120000.');
  let DatabaseSync;
  try { ({ DatabaseSync } = await import('node:sqlite')); }
  catch { throw failure('OUTBOX_UNAVAILABLE', 'SQLite outbox requires a Node runtime with node:sqlite enabled (Node 22.13 or later).'); }
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const root = await realpath(directory);
  await mkdir(join(root, 'locks'), { mode: 0o700, recursive: true });
  const path = join(root, 'intents.sqlite');
  let closed = false, operations = 0;

  async function operation(work) {
    if (closed) throw failure('OUTBOX_CLOSED', 'The print outbox storage was closed.');
    operations++;
    try { return await work(); } finally { operations--; }
  }

  async function acquire(file, deadline = performance.now() + lockTimeoutMs) {
    for (;;) {
      let db;
      try {
        db = new DatabaseSync(file);
        db.exec('PRAGMA busy_timeout=0; PRAGMA synchronous=FULL; PRAGMA trusted_schema=OFF; BEGIN IMMEDIATE;');
        return db;
      } catch (error) {
        db?.close();
        if (!busy(error)) throw error;
        if (performance.now() >= deadline) throw failure('OUTBOX_BUSY', 'Another process is using this print outbox. Retry after it finishes.');
        await delay(Math.min(25, Math.max(1, deadline - performance.now())));
      }
    }
  }

  async function transaction(work) {
    return operation(async () => {
      const deadline = performance.now() + lockTimeoutMs;
      const db = await acquire(path, deadline);
      try {
        const result = work(db);
        for (;;) {
          try { db.exec('COMMIT'); break; }
          catch (error) {
            // SQLITE_BUSY leaves this transaction active. Another connection's
            // brief read can block COMMIT even after BEGIN IMMEDIATE succeeded.
            // Retry only COMMIT: rerunning work could apply an update twice.
            if (error?.code !== 'ERR_SQLITE_ERROR' || (error.errcode & 255) !== 5) throw error;
            if (performance.now() >= deadline) throw failure('OUTBOX_BUSY', 'The print outbox could not commit before the storage lock deadline. Retry after the other reader finishes.');
            await delay(Math.min(25, Math.max(1, deadline - performance.now())));
          }
        }
        return result;
      } catch (error) {
        try { db.exec('ROLLBACK'); } catch { /* Preserve the original storage failure. */ }
        throw error;
      } finally { db.close(); }
    });
  }

  await transaction(db => {
    const version = db.prepare('PRAGMA user_version').get().user_version;
    if (version === 0) {
      if (db.prepare("SELECT count(*) AS count FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").get().count)
        throw failure('OUTBOX_CORRUPT', 'The outbox path contains an unrelated database.');
      db.exec(`CREATE TABLE intents (
        local_id INTEGER PRIMARY KEY AUTOINCREMENT,
        service_id TEXT NOT NULL, intent_key TEXT NOT NULL,
        version INTEGER NOT NULL, fingerprint TEXT NOT NULL, record TEXT NOT NULL,
        UNIQUE(service_id, intent_key)
      ); PRAGMA user_version=1;`);
    } else if (version !== 1) throw failure('OUTBOX_UNSUPPORTED', 'This outbox database version is unsupported.');
  });

  function decode(row) {
    let record;
    try { record = JSON.parse(row.record); } catch { throw failure('OUTBOX_CORRUPT', 'A saved outbox record is unreadable.'); }
    if (!record || record.localId !== row.local_id || record.serviceId !== row.service_id ||
        record.idempotencyKey !== row.intent_key || record.version !== row.version || record.fingerprint !== row.fingerprint)
      throw failure('OUTBOX_CORRUPT', 'A saved outbox record does not match its identity.');
    return record;
  }

  return Object.freeze({
    list() { return transaction(db => db.prepare('SELECT * FROM intents ORDER BY local_id').all().map(decode)); },
    add(record) {
      const copy = structuredClone(record);
      return transaction(db => {
        const prior = db.prepare('SELECT * FROM intents WHERE service_id=? AND intent_key=?').get(copy.serviceId, copy.idempotencyKey);
        if (prior) return decode(prior);
        if (db.prepare('SELECT count(*) AS count FROM intents').get().count >= 10000)
          throw failure('OUTBOX_FULL', 'The print outbox is full; existing intents were preserved.');
        const inserted = db.prepare('INSERT INTO intents(service_id,intent_key,version,fingerprint,record) VALUES(?,?,1,?,?)')
          .run(copy.serviceId, copy.idempotencyKey, copy.fingerprint, '{}');
        const saved = { ...copy, localId: Number(inserted.lastInsertRowid), version: 1 };
        db.prepare('UPDATE intents SET record=? WHERE local_id=?').run(JSON.stringify(saved), saved.localId);
        return saved;
      });
    },
    update(previous, next) {
      const before = structuredClone(previous), after = structuredClone(next);
      return transaction(db => {
        const row = db.prepare('SELECT * FROM intents WHERE local_id=?').get(before.localId);
        const current = row && decode(row);
        if (!current || current.version !== before.version || current.fingerprint !== before.fingerprint ||
            ['serviceId', 'idempotencyKey', 'printer', 'fingerprint'].some(key => current[key] !== before[key] || after[key] !== current[key]))
          throw failure('OUTBOX_CONFLICT', 'The saved print intent changed during recovery.');
        const saved = { ...after, localId: current.localId, version: current.version + 1 };
        db.prepare('UPDATE intents SET version=?,record=? WHERE local_id=?').run(saved.version, JSON.stringify(saved), saved.localId);
        return saved;
      });
    },
    withQueue(serviceId, printer, work) {
      return operation(async () => {
        const key = createHash('sha256').update(JSON.stringify([serviceId, printer])).digest('hex');
        // This separate database holds only the dispatch lock. Intent commits use
        // their own database, so a crash cannot roll back bytes already submitted.
        const lock = await acquire(join(root, 'locks', key + '.sqlite'));
        try { return await work(); }
        finally { try { lock.exec('ROLLBACK'); } finally { lock.close(); } }
      });
    },
    close() {
      if (operations) throw failure('OUTBOX_BUSY', 'Wait for outbox operations to finish before closing storage.');
      closed = true;
    }
  });
}
