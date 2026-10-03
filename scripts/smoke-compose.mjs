import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';

const base = (process.env.API_BASE ?? 'http://127.0.0.1:8080').replace(/\/$/, '');
const key = process.env.APP_API_KEY;
if (!key) throw new Error('APP_API_KEY must be loaded from the local .env file.');

const output = process.env.SMOKE_OUTPUT ?? 'artifacts/docker-live-smoke.json';
const resume = process.argv.includes('--resume');
const report = resume
  ? JSON.parse(await fs.readFile(`${output}.partial.json`, 'utf8'))
  : { artifactType: 'docker-live-integration-smoke', status: 'running', startedAt: new Date().toISOString(), steps: [] };
report.status = 'running';
delete report.error;
async function save() {
  await fs.mkdir(path.dirname(output), { recursive: true });
  await fs.writeFile(`${output}.partial.json`, JSON.stringify(report, null, 2));
}
async function step(name, action) {
  const previous = report.steps.find(item => item.name === name);
  if (previous) { console.log(`REUSE ${name}`); return previous; }
  const started = performance.now();
  console.log(`START ${name}`);
  const result = await action();
  report.steps.push({ name, durationMs: Math.round(performance.now() - started), ...result });
  await save();
  console.log(`PASS ${name}`);
  return result;
}
async function request(route, body, extraHeaders = {}) {
  const content = await new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : JSON.stringify(body);
    const request = http.request(new URL(`${base}${route}`), {
      method: payload === undefined ? 'GET' : 'POST',
      headers: { 'Content-Type': 'application/json', 'X-API-Key': key, ...extraHeaders }
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const content = Buffer.concat(chunks).toString('utf8');
        if (response.statusCode < 200 || response.statusCode >= 300)
          reject(new Error(`${route}: HTTP ${response.statusCode}: ${content.slice(0, 400)}`));
        else resolve(content);
      });
      response.on('error', reject);
    });
    request.setTimeout(900_000, () => request.destroy(new Error(`${route}: 15-minute response timeout`)));
    request.on('error', reject);
    request.end(payload);
  });
  return content;
}
function rpcJson(content) {
  const data = content.split(/\r?\n/).find(line => line.startsWith('data:'));
  return JSON.parse(data ? data.slice(5).trim() : content);
}

try {
  const health = await step('native-aot-ready', async () => {
    const result = JSON.parse(await request('/health/ready'));
    if (result.nativeAot !== true || result.graphStore !== 'neo4j') throw new Error('Expected Native AOT and Neo4j.');
    return { nativeAot: result.nativeAot, graphStore: result.graphStore, provider: result.provider };
  });
  report.health = health;
  const corpus = JSON.parse(await fs.readFile('data/demo-corpus.json', 'utf8'));
  for (const document of corpus) {
    await step(`ingest:${document.source}`, async () => {
      const result = JSON.parse(await request('/api/v1/documents', document));
      return { documentId: result.documentId, chunks: result.chunks, entities: result.entities, relations: result.relations };
    });
  }
  await step('neo4j-leiden-communities', async () => {
    const result = JSON.parse(await request('/api/v1/communities', {
      gamma: 1, theta: 0.01, randomSeed: 19, maxLevels: 10
    }));
    return { levels: result.levels, communities: result.communities, algorithm: result.algorithm };
  });
  const question = 'What shared strategic risks affect Orion?';
  await step('maf-global-answer', async () => {
    const result = JSON.parse(await request('/api/v1/query', { question, mode: 'global', topK: 5, hops: 2 }));
    if (!result.citations?.length || !result.reflection?.accepted) throw new Error('Global answer lacked accepted, cited evidence.');
    return { citations: result.citations.length, reflection: result.reflection.support, accepted: result.reflection.accepted,
      traceId: result.traceId, answer: result.answer };
  });
  const rpcHeaders = { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-11-25' };
  await step('mcp-http-discovery', async () => {
    const initialized = rpcJson(await request('/mcp', { jsonrpc: '2.0', id: 1, method: 'initialize', params: {
      protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'docker-smoke', version: '1.0.0' }
    } }, rpcHeaders));
    const listed = rpcJson(await request('/mcp', { jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} }, rpcHeaders));
    const tools = listed.result?.tools?.map(tool => tool.name) ?? [];
    if (!initialized.result?.serverInfo || !tools.includes('rag_query')) throw new Error('MCP tool discovery failed.');
    return { protocolVersion: initialized.result.protocolVersion, tools };
  });
  report.status = 'passed';
  report.completedAt = new Date().toISOString();
  await fs.writeFile(output, JSON.stringify(report, null, 2));
  await fs.rm(`${output}.partial.json`, { force: true });
  console.log(`PASS docker live smoke: ${output}`);
} catch (error) {
  report.status = 'failed';
  report.error = String(error);
  await save();
  console.error(error);
  process.exitCode = 1;
}
