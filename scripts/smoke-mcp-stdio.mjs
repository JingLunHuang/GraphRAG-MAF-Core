import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import fs from 'node:fs/promises';

const dotnet = process.env.DOTNET_EXE ?? (process.platform === 'win32' ? '.runtime/dotnet/dotnet.exe' : 'dotnet');
const dll = process.env.CLI_DLL ?? 'src/GraphRag.Cli/bin/Debug/net10.0/GraphRag.Cli.dll';
const child = spawn(dotnet, [dll, 'mcp', '--fixture'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const pending = new Map();
let stderr = '';
child.stderr.on('data', data => { stderr += data.toString(); });
createInterface({ input: child.stdout }).on('line', line => {
  try { const message = JSON.parse(line); if (message.id && pending.has(message.id)) { pending.get(message.id).resolve(message); pending.delete(message.id); } }
  catch { for (const item of pending.values()) item.reject(new Error('Non-JSON content on MCP stdout.')); }
});
let id = 0;
async function rpc(method, params) {
  const requestId = ++id;
  const result = new Promise((resolve, reject) => { pending.set(requestId, { resolve, reject }); });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: requestId, method, params }) + '\n');
  const message = await Promise.race([result, new Promise((_, reject) => {
    const timer = setTimeout(() => reject(new Error(`MCP ${method} timed out.`)), 30000); timer.unref();
  })]);
  if (message.error) throw new Error(JSON.stringify(message.error));
  return message.result;
}
try {
  const initialization = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'stdio-smoke', version: '1.0.0' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const tools = await rpc('tools/list', {});
  const corpus = JSON.parse(await fs.readFile('data/demo-corpus.json', 'utf8'));
  for (const document of corpus) {
    const ingested = await rpc('tools/call', { name: 'rag_ingest', arguments: document });
    if (ingested.isError) throw new Error('MCP ingestion failed.');
  }
  const response = await rpc('tools/call', { name: 'rag_query', arguments: { question: 'Northwind Orion Contoso?', mode: 'local' } });
  if (response.isError) throw new Error('MCP query failed.');
  const answer = JSON.parse(response.content[0].text);
  if (!answer.reflection.accepted || !answer.citations.length) throw new Error('MCP answer failed critic/citation checks.');
  const report = { artifactType: 'mcp-stdio-engineering-smoke', status: 'passed', protocol: initialization.protocolVersion,
    tools: tools.tools.map(tool => tool.name), documents: corpus.length, citations: answer.citations.length };
  await fs.mkdir('artifacts', { recursive: true });
  await fs.writeFile('artifacts/mcp-stdio-smoke.json', JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
} catch (error) { console.error(stderr.slice(-1500)); throw error; }
finally { child.stdin.end(); child.kill(); }
