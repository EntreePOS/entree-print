// Runs current POS wrappers with fake C-Lodop/server responses. Never prints or opens sockets.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const posRoot = process.argv[2];
if (!posRoot) throw new Error('Pass the Entree POS repository path.');

(async () => {
  const creates = [], updates = [], observations = [];
  let submissions = 0;
  const win = { Printer: { config: { print: {} }, constructor: { ready: async () => {} } } };
  win.asyncSocket = async (event, payload) => {
    if (event === '[SPOOLER] CREATE') { creates.push(payload); return { _id: 'audit-' + creates.length }; }
    if (event === '[SPOOLER] UPDATE') updates.push(payload);
    return { ok: true };
  };
  const context = vm.createContext({ window: win, document: { currentScript: null }, module: { exports: {} },
    console, URL, setTimeout: (fn, ms) => setTimeout(fn, Math.min(ms, 30)), clearTimeout,
    simulatedSubmit: () => ++submissions });
  vm.runInContext(fs.readFileSync(path.join(posRoot, 'src/renderer/plugin/printer/clodop-patch-loader.js'), 'utf8'), context);
  const vendorStub = `window.CLODOP = {
    CVERSION: 'fake', PRINT_INIT() {}, SET_PRINT_MODE() {},
    PRINT() { return 'callback-task-' + simulatedSubmit(); }
  }; window.getCLodop = () => window.CLODOP;`;
  vm.runInContext(context.module.exports.patch(vendorStub), context);
  const source = fs.readFileSync(path.join(posRoot, 'src/renderer/plugin/print/status.js'), 'utf8');
  const api = vm.runInContext(source.slice(0, source.lastIndexOf('export {')) +
    '\n({ printHtmlTicket, reconcileSpoolerJobsOnRefresh })', context);
  const meta = { station: 'fake-station', printer: 'fake-printer', orderID: 'fake-order', source: 'ticket', html: '<p>Fake ticket</p>' };
  await assert.rejects(api.printHtmlTicket(meta, () => {}), /timed out waiting for C-Lodop callback/);
  assert.equal(submissions, 1);
  assert.equal(updates.at(-1).status, 'failed');
  observations.push({ scenario: 'POS_lost_print_callback', simulatedAcceptances: submissions,
    auditStatus: updates.at(-1).status, result: 'accepted fake output is labelled failed, without retained C-Lodop job identity' });

  // Explicitly model an operator retry after the first call is reported failed.
  // The wrapper does not retry PRINT itself in this scenario.
  await assert.rejects(api.printHtmlTicket(meta, () => {}), /timed out/);
  assert.equal(submissions, 2);
  assert.notEqual(creates[0].clientRequestId, creates[1].clientRequestId);
  observations.push({ scenario: 'POS_operator_retries_after_lost_callback', simulatedAcceptances: submissions,
    distinctAuditKeys: true, result: 'second receipt call creates a new audit key and submits again' });

  win.CLODOP_API = {
    getSystemInfo: async () => '0', setPrinter: () => {},
    getJobStatus: async () => ({ classification: { terminal: false, state: 'tracking' } }),
    raw: () => ({ GET_PRINTER_INDEX: () => 0, SET_PRINTER_INDEX: () => {} })
  };
  await api.reconcileSpoolerJobsOnRefresh([{ _id: 'empty-queue-job', printer: 'fake-printer', status: 'submitted', clodopJobId: 'fake-driver-id' }]);
  const inferred = updates.find(update => update._id === 'empty-queue-job');
  assert.equal(inferred?.status, 'printed');
  assert.equal(inferred?.snapshot?.inferred, true);
  observations.push({ scenario: 'POS_empty_queue_without_completion_evidence', auditStatus: inferred.status,
    result: 'job becomes printed on queue emptiness alone' });

  const output = JSON.stringify({ characterization: true, physicalPrinting: false, observations }, null, 2);
  console.log(output);
  if (process.argv[3]) fs.writeFileSync(process.argv[3], output + '\n');
})().catch(error => { console.error(error); process.exitCode = 1; });
