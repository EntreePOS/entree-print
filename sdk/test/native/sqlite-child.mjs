import { createSqliteOutboxStorage } from '../../sqlite-outbox.mjs';

const [directory, mode] = process.argv.slice(2);
const store = await createSqliteOutboxStorage({ directory });
const record = { serviceId:'owner', idempotencyKey:'one', printer:'cashier', fingerprint:'source',
  content:'厨房', state:'pending', wire:null };
if (mode === 'add') {
  await store.add(record);
  store.close(); process.disconnect();
} else {
  await store.withQueue('owner','cashier',async () => {
    const saved = await store.add(record);
    await store.update(saved,{...saved,state:'submitting',wire:{json:'exact saved bytes',digest:'saved digest'}});
    process.send({state:'locked'});
    await new Promise(() => { setInterval(() => {}, 1000); });
  });
}
