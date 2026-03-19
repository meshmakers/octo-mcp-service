#!/usr/bin/env node
const https = require('https');

// Disable SSL verification for localhost
process.env["NODE_TLS_REJECT_UNAUTHORIZED"] = 0;

const tenant = process.env.OCTO_TENANT || 'sbeg';
const host = process.env.OCTO_HOST || 'localhost';
const port = parseInt(process.env.OCTO_PORT || '5017');

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

  const options = {
    hostname: host,
    port: port,
    path: `/${tenant}/mcp`,
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
      'Cache-Control': 'no-cache',
      'Content-Length': Buffer.byteLength(postData)
    }
  };

  const req = https.request(options, (res) => {
    let responseData = '';

    res.on('data', (chunk) => {
      responseData += chunk;
    });

    res.on('end', () => {
      // Parse SSE format and extract JSON
      const lines = responseData.split('\n');

      for (const line of lines) {
        if (line.startsWith('data: ')) {
          const jsonData = line.substring(6).trim(); // Remove "data: " prefix
          if (jsonData && jsonData.startsWith('{')) {
            process.stdout.write(jsonData + '\n');
            break; // Send only the first valid JSON response
          }
        }
      }
    });
  });

  req.on('error', (e) => {
    console.error('Request error:', e);
  });

  req.write(postData);
  req.end();
}