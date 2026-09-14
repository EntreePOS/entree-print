// POS-owned storage. No credentials, network listeners or business routing live here.
function failure(code, message) { return Object.assign(new Error(message), { code, delivery: 'not_sent' }); }
const clone = value => JSON.parse(JSON.stringify(value));

export function createIndexedDbOutboxStorage({ name = 'entree-print-outbox', indexedDB = globalThis.indexedDB,
  locks = globalThis.navigator?.locks } = {}) {
  let opening;
  function open() {
    if (!indexedDB || !locks) throw failure('OUTBOX_UNAVAILABLE', 'Persistent printing requires IndexedDB and Web Locks in this POS context.');
    return opening ??= new Promise((resolve, reject) => {
      const request = indexedDB.open(name, 1);
      request.onupgradeneeded = () => {
        const store = request.result.createObjectStore('intents', { keyPath: 'localId', autoIncrement: true });
        store.createIndex('intent', ['serviceId', 'idempotencyKey'], { unique: true });
      };
      request.onsuccess = () => {
        const db = request.result;
        db.onversionchange = () => { db.close(); opening = null; };
        resolve(db);
      };
      request.onerror = () => { opening = null; reject(request.error); };
      request.onblocked = () => { opening = null; reject(failure('OUTBOX_UNAVAILABLE', 'Another POS page is blocking print storage.')); };
    });
  }
  async function transaction(mode, work) {
    const db = await open();
    return new Promise((resolve, reject) => {
      let result, error;
      // Older Chromium may ignore the durability hint. Always await transaction completion.
      const tx = db.transaction('intents', mode, { durability: 'strict' });
      tx.oncomplete = () => resolve(result);
      tx.onabort = () => reject(error || tx.error || failure('OUTBOX_STORAGE_FAILED', 'Print storage transaction was aborted.'));
      tx.onerror = () => {};
      try { work(tx.objectStore('intents'), value => { result = value; }, reason => { error = reason; tx.abort(); }); }
      catch (reason) { error = reason; tx.abort(); }
    });
  }
  return Object.freeze({
    async withQueue(serviceId, printer, work) {
      await open();
      return locks.request(JSON.stringify([name, serviceId, printer]), { mode: 'exclusive' }, work);
    },
    list() { return transaction('readonly', (store, done) => { store.getAll().onsuccess = event => done(event.target.result); }); },
    add(record) {
      return transaction('readwrite', (store, done, abort) => {
        store.index('intent').get([record.serviceId, record.idempotencyKey]).onsuccess = event => {
          if (event.target.result) { done(event.target.result); return; }
          store.count().onsuccess = count => {
            if (count.target.result >= 10000) { abort(failure('OUTBOX_FULL', 'The POS print outbox is full; existing intents were preserved.')); return; }
            store.add({ ...record, version: 1 }).onsuccess = added => done({ ...record, version: 1, localId: added.target.result });
          };
        };
      });
    },
    update(previous, next) {
      return transaction('readwrite', (store, done, abort) => {
        store.get(previous.localId).onsuccess = event => {
          const current = event.target.result;
          if (!current || current.version !== previous.version || current.fingerprint !== previous.fingerprint) {
            abort(failure('OUTBOX_CONFLICT', 'The saved print intent changed during recovery.')); return;
          }
          const saved = { ...next, localId: current.localId, version: current.version + 1 };
          store.put(saved).onsuccess = () => done(saved);
        };
      });
    },
    async close() { if (opening) (await opening).close(); opening = null; }
  });
}

// The SDK supplies validated preparation and exact-byte submission. Storage can be
// replaced by a POS-owned transactional implementation with the same contract.
export function createOutboxController(storage, transport) {
  async function saved(action) {
    try { return await action(); }
    catch (cause) { throw failure(cause.code?.startsWith?.('OUTBOX_') ? cause.code : 'OUTBOX_STORAGE_FAILED', cause.message); }
  }
  async function changeIntent(selector, action) {
    if (!selector || typeof selector.serviceId !== 'string' || typeof selector.idempotencyKey !== 'string' ||
      Object.keys(selector).some(key => !['serviceId', 'idempotencyKey'].includes(key)))
      throw failure('REQUEST_INVALID', 'Identify the local intent with serviceId and idempotencyKey.');
    const find = records => records.find(record => record.serviceId === selector.serviceId && record.idempotencyKey === selector.idempotencyKey);
    const record = find(await saved(() => storage.list()));
    if (!record) throw failure('OUTBOX_NOT_FOUND', 'The local print intent was not found.');
    return saved(() => storage.withQueue(record.serviceId, record.printer, async () => {
      const current = find(await storage.list());
      return clone(await storage.update(current, action(current)));
    }));
  }
  return Object.freeze({
    async enqueue(value) {
      // Snapshot before the first await so checkout mutations cannot edit the ticket.
      const source = transport.validate(value);
      const fingerprint = await transport.fingerprint(source);
      const result = await saved(() => storage.add({ ...source, fingerprint, state: 'pending', wire: null, mayHaveAccepted: false,
        createdAt: new Date().toISOString(), job: null, error: null }));
      if (result.fingerprint !== fingerprint) throw failure('OUTBOX_CONFLICT', 'This intent key already belongs to a different ticket.');
      return clone(result);
    },
    list() { return saved(() => storage.list()).then(clone); },
    retry(selector) {
      return changeIntent(selector, record => {
        if (['accepted', 'cancelled'].includes(record.state)) throw failure('OUTBOX_TERMINAL', 'This local intent is already finished.');
        return { ...record, state: record.wire ? 'uncertain' : 'pending', error: null };
      });
    },
    cancel(selector) {
      return changeIntent(selector, record => {
        if (record.wire || record.state === 'accepted') throw failure('OUTBOX_MAY_HAVE_PRINTED', 'This intent has a saved submission; reconcile its owning service before taking another action.');
        return { ...record, state: 'cancelled', error: null };
      });
    },
    stopRetrying(selector) {
      return changeIntent(selector, record => {
        if (['accepted', 'cancelled'].includes(record.state)) throw failure('OUTBOX_TERMINAL', 'This local intent is already finished.');
        // An explicit operator action releases this local queue only. A request
        // already in the service or Windows may still print; retain all evidence.
        return { ...record, state: 'stopped' };
      });
    },
    async flush(library) {
      const owner = transport.owner(library); // Reject foreign/disconnected libraries before touching the queue.
      const all = await saved(() => storage.list());
      const printers = [...new Set(all.filter(record => record.serviceId === owner && !['accepted', 'cancelled', 'stopped'].includes(record.state)).map(record => record.printer))];
      const results = await Promise.all(printers.map(printer => saved(() => storage.withQueue(owner, printer, async () => {
        const records = (await storage.list()).filter(record => record.serviceId === owner && record.printer === printer)
          .sort((a, b) => a.localId - b.localId);
        const outcomes = [];
        for (let record of records) {
          if (['accepted', 'cancelled', 'stopped'].includes(record.state)) continue;
          // Unresolved hard failures block later receipts for this destination, not other printers.
          if (record.state === 'needs_attention') { outcomes.push(clone(record)); break; }
          if (await transport.fingerprint(transport.validate(record, true)) !== record.fingerprint)
            throw failure('OUTBOX_CORRUPT', 'A saved receipt failed its content check.');
          const previouslyUncertain = record.mayHaveAccepted === true || ['submitting', 'uncertain'].includes(record.state) && record.mayHaveAccepted !== false;
          try {
            if (!record.wire) {
              const wire = await transport.prepare(library, record);
              record = await saved(() => storage.update(record, { ...record, wire, state: 'prepared', error: null }));
            }
            // This commit must precede any job POST, including the very first attempt.
            record = await saved(() => storage.update(record, { ...record, state: 'submitting', mayHaveAccepted: true, error: null }));
            const job = await transport.submit(library, record);
            record = await saved(() => storage.update(record, { ...record, state: 'accepted', job, error: null }));
            outcomes.push(clone(record));
          } catch (error) {
            // A storage error may occur after the plugin accepted. Never replace its saved wire.
            if (error.code?.startsWith?.('OUTBOX_')) throw error;
            const mayHaveAccepted = previouslyUncertain || error.delivery !== 'not_sent';
            if (mayHaveAccepted) error.delivery = 'unknown';
            const state = error.delivery === 'unknown' ? 'uncertain' : error.retryable ? (record.wire ? 'prepared' : 'pending') : 'needs_attention';
            record = await saved(() => storage.update(record, { ...record, state, mayHaveAccepted,
              error: { code: error.code || 'PRINT_FAILED', message: error.message, delivery: error.delivery || 'unknown' } }));
            outcomes.push(clone(record));
            break;
          }
        }
        return outcomes;
      }))));
      return results.flat();
    }
  });
}
