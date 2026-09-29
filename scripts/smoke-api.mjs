import fs from 'node:fs/promises';
import path from 'node:path';

const base = (process.env.API_BASE ?? 'http://localhost:8080').replace(/\/$/, '');
const headers = { 'Content-Type': 'application/json', 'X-API-Key': process.env.APP_API_KEY ?? '' };
async function request(route, body, extraHeaders = {}) {
  const response = await fetch(base + route, { method: body === undefined ? 'GET' : 'POST', headers: { ...headers, ...extraHeaders },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(60000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${route} HTTP ${response.status}: ${text.slice(0, 300)}`);
  return { status: response.status, text, headers: response.headers };
}
function check(value, message) { if (!value) throw new Error(message); }
function rpcJson(text) {
  const data = text.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => line.slice(5).trim());
  return JSON.parse(data.length ? data[0] : text);
}

const health = JSON.parse((await request('/health/ready')).text);
if (process.env.REQUIRE_NATIVE_AOT === 'true') check(health.nativeAot === true, 'Server must be an actual Native AOT binary.');
const corpus = JSON.parse(await fs.readFile('data/demo-corpus.json', 'utf8'));
const ingested = [];
for (const document of corpus) ingested.push(JSON.parse((await request('/api/v1/documents', document)).text));
const communities = JSON.parse((await request('/api/v1/communities', { gamma: 1, theta: 0.01, randomSeed: 19, maxLevels: 10 })).text);
const query = { question: 'How can Fabrikam maintenance affect Northwind Orion releases?', topK: 2, hops: 2 };
const modes = {};
for (const mode of ['local', 'global', 'naive']) {
  const answer = JSON.parse((await request('/api/v1/query', { ...query, mode })).text);
  check(answer.citations.length > 0, `${mode} returned no citations.`);
  if (mode !== 'naive') check(answer.reflection.accepted === true, `${mode} critic did not accept.`);
  modes[mode] = { citations: answer.citations.length, accepted: answer.reflection.accepted, traceId: answer.traceId };
}
const stream = (await request('/api/v1/query/stream', { ...query, mode: 'local' })).text;
check(stream.includes('event: agent_delta'), 'SSE has no agent deltas.');
check(stream.includes('event: result'), 'SSE has no final answer.');
check(!stream.includes('event: error'), 'SSE reported an error.');
const concurrent = await Promise.all(Array.from({ length: 16 }, () => request('/api/v1/query', { ...query, mode: 'local' })));
check(concurrent.every(item => JSON.parse(item.text).reflection.accepted), 'Concurrent query failed.');
const rpcHeaders = { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-11-25' };
const initialize = rpcJson((await request('/mcp', { jsonrpc: '2.0', id: 1, method: 'initialize', params: {
  protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'graphrag-smoke', version: '1.0.0' }
} }, rpcHeaders)).text);
check(initialize.result?.serverInfo, 'MCP initialize failed.');
const listed = rpcJson((await request('/mcp', { jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} }, rpcHeaders)).text);
check(listed.result?.tools.some(tool => tool.name === 'rag_query'), 'MCP discovery failed.');
const called = rpcJson((await request('/mcp', { jsonrpc: '2.0', id: 3, method: 'tools/call', params: {
  name: 'rag_query', arguments: { question: query.question, mode: 'local' }
} }, rpcHeaders)).text);
check(called.result && !called.result.isError, 'MCP query tool failed.');
const mcpAnswer = JSON.parse(called.result.content.find(block => block.type === 'text').text);
check(mcpAnswer.citations.length > 0, 'MCP answer has no citations.');
const report = { artifactType: 'api-mcp-engineering-smoke', status: 'passed', health, documents: ingested.length,
  communities, modes, sse: 'passed', concurrentQueries: concurrent.length, mcp: { protocol: initialize.result.protocolVersion,
    tools: listed.result.tools.map(tool => tool.name), citations: mcpAnswer.citations.length } };
const output = process.env.SMOKE_OUTPUT ?? 'artifacts/api-smoke.json';
await fs.mkdir(path.dirname(output), { recursive: true });
await fs.writeFile(output, JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
