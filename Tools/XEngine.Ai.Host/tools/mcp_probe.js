// Probe helper: frames MCP JSON-RPC messages over stdio and parses framed responses.
// Usage: node mcp_probe.js <dll-or-exe-and-args...>
// Sends initialize -> notifications/initialized -> tools/list, prints the tools/list result.
const { spawn } = require('child_process');
const fs = require('fs');

const hostArgs = process.argv.slice(2);
if (hostArgs.length === 0) {
  console.error('usage: node mcp_probe.js <spawn-args>');
  process.exit(2);
}

const reqs = [
  { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'probe', version: '1.0' } } },
  { jsonrpc: '2.0', method: 'notifications/initialized' },
  { jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} },
];

function frame(obj) {
  // The MCP SDK's StreamServerTransport reads newline-delimited JSON-RPC over stdio
  // (not LSP-style Content-Length framing). Each message is one JSON object per line.
  return JSON.stringify(obj) + '\n';
}

const child = spawn('dotnet', hostArgs, { stdio: ['pipe', 'pipe', 'inherit'] });
let buffer = Buffer.alloc(0);
const responses = [];

child.stdout.on('data', (chunk) => {
  buffer = Buffer.concat([buffer, chunk]);
  // Parse newline-delimited JSON-RPC responses.
  let nl;
  while ((nl = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, nl).toString('utf8').trim();
    buffer = buffer.slice(nl + 1);
    if (!line) continue;
    try { responses.push(JSON.parse(line)); } catch (e) { /* partial; wait for more */ }
  }
});

child.on('close', () => {
  const toolsList = responses.find((r) => r.id === 2);
  if (toolsList) {
    const names = (toolsList.result && toolsList.result.tools || []).map((t) => t.name);
    console.log('tools/list OK — ' + names.length + ' tools registered:');
    names.forEach((n) => console.log('  - ' + n));
  } else {
    console.log('NO tools/list response. Raw responses: ' + responses.length);
    responses.forEach((r) => console.log(JSON.stringify(r)));
  }
});

// Send all requests up front.
child.stdin.write(reqs.map(frame).join(''));
// Give the server a moment, then close stdin so it exits.
setTimeout(() => { try { child.stdin.end(); } catch (e) {} }, 1500);
