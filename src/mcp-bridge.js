#!/usr/bin/env node
const https = require('https');
const fs = require('fs');

// Disable SSL verification for localhost dev cert
process.env["NODE_TLS_REJECT_UNAUTHORIZED"] = 0;

const tenant = process.env.OCTO_TENANT || 'sbeg';
const host = process.env.OCTO_HOST || 'localhost';
const port = parseInt(process.env.OCTO_PORT || '5017');
const debugLog = process.env.OCTO_MCP_BRIDGE_LOG || '/tmp/mcp-bridge.log';

// MCP Streamable HTTP transport is stateful — server returns Mcp-Session-Id on
// initialize, client must echo it on every subsequent request.
let sessionId = null;

function logDebug(direction, payload) {
  try {
    fs.appendFileSync(debugLog, `[${new Date().toISOString()}] ${direction} ${payload}\n`);
  } catch (_) { /* best-effort */ }
}

let buffer = '';

process.stdin.on('data', (chunk) => {
  buffer += chunk.toString();

  const lines = buffer.split('\n');
  buffer = lines.pop();

  lines.forEach(line => {
    if (line.trim()) {
      sendToServer(line);
    }
  });
});

function sendToServer(jsonData) {
  const postData = jsonData;
  logDebug('>>>req', `(session=${sessionId || 'none'}) ${postData}`);

  const headers = {
    'Content-Type': 'application/json',
    'Accept': 'application/json, text/event-stream',
    'Cache-Control': 'no-cache',
    'Content-Length': Buffer.byteLength(postData)
  };
  if (sessionId) {
    headers['Mcp-Session-Id'] = sessionId;
  }

  const options = {
    hostname: host,
    port: port,
    path: `/${tenant}/mcp`,
    method: 'POST',
    headers
  };

  const req = https.request(options, (res) => {
    // Capture the session id from the initialize response. The header is
    // case-insensitive; Node lower-cases all incoming header names.
    if (!sessionId && res.headers['mcp-session-id']) {
      sessionId = res.headers['mcp-session-id'];
      logDebug('<<<hdr', `captured Mcp-Session-Id=${sessionId}`);
    }

    let responseData = '';

    res.on('data', (chunk) => {
      responseData += chunk;
    });

    res.on('end', () => {
      logDebug('<<<res', `HTTP ${res.statusCode} ${responseData.substring(0, 400)}${responseData.length > 400 ? '…' : ''}`);

      // Parse SSE format and extract every JSON-RPC payload in the stream.
      // The previous version broke after the first frame, dropping later
      // frames in multi-message responses.
      const lines = responseData.split('\n');
      for (const line of lines) {
        if (line.startsWith('data: ')) {
          const jsonData = line.substring(6).trim();
          if (jsonData && jsonData.startsWith('{')) {
            process.stdout.write(jsonData + '\n');
          }
        }
      }
    });
  });

  req.on('error', (e) => {
    logDebug('<<<err', `${e.message}`);
    console.error('Request error:', e);
  });

  req.write(postData);
  req.end();
}
