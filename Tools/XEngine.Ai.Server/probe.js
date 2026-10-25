// PoC probe for the in-process AiServer: initialize, list tools, then CALL runtime_health.
// Proves the server runs in-process + the MainThreadDispatcher marshals the tool onto the loop.
const { spawn } = require('child_process');

const args = process.argv.slice(2);
if (!args.length) { console.error('usage: node probe.js <spawn-args>'); process.exit(2); }

const reqs = [
  { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'probe', version: '1.0' } } },
  { jsonrpc: '2.0', method: 'notifications/initialized' },
  { jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} },
  { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'runtime_health', arguments: {} } },
];
const frame = (o) => JSON.stringify(o) + '\n';

const child = spawn('dotnet', args, { stdio: ['pipe', 'pipe', 'inherit'] });
let buf = Buffer.alloc(0);
const resps = [];
child.stdout.on('data', (c) => {
  buf = Buffer.concat([buf, c]);
  let nl;
  while ((nl = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, nl).toString('utf8').trim();
    buf = buf.slice(nl + 1);
    if (!line) continue;
    try { resps.push(JSON.parse(line)); } catch (e) {}
  }
});
child.on('error', (e) => console.error('spawn error', e));
child.on('close', (code) => {
  const list = resps.find((r) => r.id === 2);
  const call = resps.find((r) => r.id === 3);
  if (list) {
    const names = (list.result && list.result.tools || []).map((t) => t.name);
    console.log('tools/list OK (' + names.length + '): ' + names.join(', '));
  } else {
    console.log('NO tools/list response. responses=' + resps.length);
  }
  if (call) {
    const txt = call.result && call.result.content && call.result.content[0] && call.result.content[0].text;
    console.log('runtime_health call OK: ' + (txt || JSON.stringify(call.result)));
  } else {
    console.log('NO runtime_health response.');
  }
  console.log('child exit code=' + code);
});

child.stdin.write(reqs.map(frame).join(''));
setTimeout(() => { try { child.stdin.end(); } catch (e) {} }, 2500);
