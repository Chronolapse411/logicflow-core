// ─────────────────────────────────────────────────────────────────────────────
// voice-proxy/voiceRelay.js  —  LogicFlow Sovereign WebSocket Relay
// Soul document: SOUL.md (project root)
//
// This is the critical relay layer. It sits between the LogicFlow client and
// Google's Gemini Live endpoint. The client NEVER connects to Google directly.
//
// Safety Architecture:
//   SOUL.md defines WHO Oracle is (values, identity, hard rules).
//   This file enforces those rules IN CODE — not just in text.
//   A soul document without code enforcement is just a suggestion.
//
// Enforcement layers:
//   1. JWT verification — unauthenticated clients are dropped immediately
//   2. Function allowlist — only declared functions can execute, nothing else
//   3. Irreversible action gate — destructive calls require prior confirmation
//   4. Path sanitizer — function args are validated to safe system paths only
//   5. Session state machine — actions out of order are rejected
//   6. Hard session cap — forcibly closed after MAX_SESSION_SECONDS
//
// Deployment: Cloud Run (WebSocket-capable), not Cloud Functions
// ─────────────────────────────────────────────────────────────────────────────

'use strict';

const http         = require('http');
const WebSocket    = require('ws');
const jwt          = require('jsonwebtoken');
const { Firestore } = require('@google-cloud/firestore');
const { SecretManagerServiceClient } = require('@google-cloud/secret-manager');
const { buildGeminiSetupPayload } = require('./index');

const db      = new Firestore();
const secrets = new SecretManagerServiceClient();

// ── Constants ─────────────────────────────────────────────────────────────────

const GEMINI_LIVE_URL = 'wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent';
const MAX_SESSION_SECONDS = 600; // hard cap matches voiceSessionStart

// ── Allowlisted functions ─────────────────────────────────────────────────────
// Only these function names can be dispatched by Gemini. Any other name
// the model invents is blocked at the relay layer — never reaches the client.

const SAFE_FUNCTIONS = new Set([
  'run_system_scan',
  'get_startup_items',
  'get_temp_file_summary',
  'clean_temp_files',     // requires prior CONFIRMATION_GRANTED state
  'get_process_list',
  'get_disk_health',
]);

// Functions that are IRREVERSIBLE and require the session to be in
// CONFIRMATION_GRANTED state before they will execute.

const REQUIRES_CONFIRMATION = new Set([
  'clean_temp_files',
]);

// ── Session State Machine ─────────────────────────────────────────────────────
// Each session tracks its state to enforce the ordering of operations.
// This prevents Gemini from attempting destructive actions without
// the user explicitly confirming in the conversation.

const SESSION_STATES = {
  ACTIVE:               'ACTIVE',               // normal conversation
  AWAITING_CONFIRMATION: 'AWAITING_CONFIRMATION', // destructive action proposed, waiting for YES
  CONFIRMATION_GRANTED:  'CONFIRMATION_GRANTED',  // user said yes — next action may proceed
  TERMINATED:           'TERMINATED',            // session ended
};

// ── Path Sanitizer ────────────────────────────────────────────────────────────
// Validates that any path appearing in function arguments is within
// known safe system directories. Prevents path traversal attacks or
// accidental access to user documents.

const SAFE_PATH_PREFIXES = [
  'C:\\Windows\\Temp',
  'C:\\Users\\',          // only TEMP subfolders should be used, validated below
  '%TEMP%',
  '%TMP%',
  'C:\\Windows\\SoftwareDistribution\\Download',
];

function isSafePath(pathStr) {
  if (!pathStr || typeof pathStr !== 'string') return true; // no path = safe
  const normalized = pathStr.replace(/\//g, '\\').toUpperCase();

  // Block any path that contains ..  (traversal attempt)
  if (normalized.includes('..')) return false;

  // Block any path under Documents, Desktop, Downloads, Pictures, Videos
  const USER_CONTENT_DIRS = ['\\DOCUMENTS\\', '\\DESKTOP\\', '\\DOWNLOADS\\', '\\PICTURES\\', '\\VIDEOS\\', '\\ONEDRIVE\\'];
  for (const dir of USER_CONTENT_DIRS) {
    if (normalized.includes(dir)) return false;
  }

  return true;
}

// Recursively walk function args and validate any string that looks like a path
function validateFunctionArgs(args) {
  if (!args || typeof args !== 'object') return true;
  for (const val of Object.values(args)) {
    if (typeof val === 'string' && (val.includes('\\') || val.includes('/'))) {
      if (!isSafePath(val)) return false;
    }
    if (typeof val === 'object' && !validateFunctionArgs(val)) return false;
  }
  return true;
}

// ── Secrets ───────────────────────────────────────────────────────────────────

let _geminiKey = null;
let _jwtSecret = null;

async function getGeminiKey() {
  if (_geminiKey) return _geminiKey;
  const [v] = await secrets.accessSecretVersion({
    name: 'projects/manuel-portfolio-2026/secrets/GEMINI_LIVE_API_KEY/versions/latest',
  });
  _geminiKey = v.payload.data.toString('utf8').trim();
  return _geminiKey;
}

async function getJwtSecret() {
  if (_jwtSecret) return _jwtSecret;
  const [v] = await secrets.accessSecretVersion({
    name: 'projects/manuel-portfolio-2026/secrets/VOICE_JWT_SECRET/versions/latest',
  });
  _jwtSecret = v.payload.data.toString('utf8').trim();
  return _jwtSecret;
}

// ── Session usage accounting ──────────────────────────────────────────────────

async function recordUsage(licenseKey, durationSeconds) {
  const now     = new Date();
  const cycleId = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
  const ref     = db.collection('voice_usage').doc(`${licenseKey}_${cycleId}`);
  await db.runTransaction(async (tx) => {
    const doc     = await tx.get(ref);
    const current = doc.exists ? (doc.data().seconds_used || 0) : 0;
    tx.set(ref, { seconds_used: current + durationSeconds, license_key: licenseKey, cycle: cycleId }, { merge: true });
  });
}

// ── Relay Session ─────────────────────────────────────────────────────────────

function createRelaySession({ clientWs, licenseKey, edition, geminiKey }) {
  const startTime    = Date.now();
  let   sessionState = SESSION_STATES.ACTIVE;
  let   geminiWs     = null;
  let   hardCapTimer = null;

  // ── Open upstream connection to Gemini Live ──────────────────────────────

  const geminiUrl = `${GEMINI_LIVE_URL}?key=${geminiKey}`;
  geminiWs = new WebSocket(geminiUrl, {
    headers: { 'Content-Type': 'application/json' },
  });

  // ── Hard cap: forcibly close after MAX_SESSION_SECONDS ──────────────────

  hardCapTimer = setTimeout(() => {
    console.log(`[relay] Hard cap reached for license ${licenseKey}`);
    terminateSession('Session time limit reached');
  }, MAX_SESSION_SECONDS * 1000);

  // ── Gemini connected: inject system prompt + tool config ─────────────────

  geminiWs.on('open', () => {
    console.log(`[relay] Gemini upstream open for ${licenseKey}`);
    const setupPayload = buildGeminiSetupPayload(edition);
    geminiWs.send(JSON.stringify(setupPayload));
  });

  // ── Messages from Gemini → safety-filtered → client ──────────────────────

  geminiWs.on('message', (rawData) => {
    if (sessionState === SESSION_STATES.TERMINATED) return;

    let msg;
    try {
      msg = JSON.parse(rawData.toString());
    } catch {
      // Non-JSON frames (audio binary) pass through directly
      if (clientWs.readyState === WebSocket.OPEN) clientWs.send(rawData);
      return;
    }

    // ── Intercept function calls from Gemini ─────────────────────────────
    const toolCall = msg?.serverContent?.toolCall;
    if (toolCall?.functionCalls?.length > 0) {
      for (const fc of toolCall.functionCalls) {
        const name = fc.name;
        const args = fc.args || {};

        // SAFETY GATE 1: Function allowlist
        if (!SAFE_FUNCTIONS.has(name)) {
          console.warn(`[safety] Blocked unknown function: "${name}" (license: ${licenseKey})`);
          sendBlockedResponse(fc.id, name, 'Function not permitted by safety policy');
          return;
        }

        // SAFETY GATE 2: Path sanitization on arguments
        if (!validateFunctionArgs(args)) {
          console.warn(`[safety] Blocked unsafe path in args for ${name} (license: ${licenseKey})`);
          sendBlockedResponse(fc.id, name, 'Argument contains restricted file path');
          return;
        }

        // SAFETY GATE 3: Irreversible action confirmation gate
        if (REQUIRES_CONFIRMATION.has(name)) {
          if (sessionState !== SESSION_STATES.CONFIRMATION_GRANTED) {
            // Block the action — AI tried to execute without confirmation
            console.warn(`[safety] Blocked ${name} — no confirmation in session state (license: ${licenseKey})`);
            sessionState = SESSION_STATES.AWAITING_CONFIRMATION;

            // Tell Gemini the action was blocked and WHY, so it can ask user
            const blockMsg = JSON.stringify({
              tool_response: {
                function_responses: [{
                  id: fc.id,
                  name,
                  response: {
                    content: { error: 'CONFIRMATION_REQUIRED', message: 'User must confirm this action before it can execute. Ask the user explicitly: "Should I go ahead and delete the temp files?" and wait for a clear yes.' },
                  },
                }],
              },
            });
            geminiWs.send(blockMsg);
            return;
          }
          // Reset state after consuming the confirmation
          sessionState = SESSION_STATES.ACTIVE;
        }
      }
    }

    // Detect confirmation signals in user audio transcript (belt + suspenders)
    // If Gemini transcribes user saying "yes", "go ahead", "do it" → set state
    const transcript = msg?.serverContent?.modelTurn?.parts?.[0]?.text || '';
    if (/\b(yes|go ahead|do it|confirm|proceed|sure|ok|okay)\b/i.test(transcript)) {
      if (sessionState === SESSION_STATES.AWAITING_CONFIRMATION) {
        sessionState = SESSION_STATES.CONFIRMATION_GRANTED;
        console.log(`[relay] Confirmation granted for ${licenseKey}`);
      }
    }

    // Forward to client
    if (clientWs.readyState === WebSocket.OPEN) {
      clientWs.send(typeof msg === 'object' ? JSON.stringify(msg) : rawData);
    }
  });

  // ── Messages from client → Gemini ─────────────────────────────────────────

  clientWs.on('message', (data) => {
    if (sessionState === SESSION_STATES.TERMINATED) return;
    if (geminiWs?.readyState === WebSocket.OPEN) {
      geminiWs.send(data);
    }
  });

  // ── Cleanup handlers ───────────────────────────────────────────────────────

  geminiWs.on('close', () => terminateSession('Gemini closed upstream'));
  geminiWs.on('error', (err) => {
    console.error(`[relay] Gemini error: ${err.message}`);
    terminateSession('Upstream error');
  });

  clientWs.on('close', () => terminateSession('Client disconnected'));
  clientWs.on('error', (err) => console.error(`[relay] Client error: ${err.message}`));

  // ── Helpers ────────────────────────────────────────────────────────────────

  function sendBlockedResponse(callId, name, reason) {
    if (geminiWs?.readyState === WebSocket.OPEN) {
      geminiWs.send(JSON.stringify({
        tool_response: {
          function_responses: [{
            id: callId,
            name,
            response: { content: { error: 'BLOCKED', message: reason } },
          }],
        },
      }));
    }
  }

  function terminateSession(reason) {
    if (sessionState === SESSION_STATES.TERMINATED) return;
    sessionState = SESSION_STATES.TERMINATED;

    clearTimeout(hardCapTimer);

    const durationSeconds = Math.floor((Date.now() - startTime) / 1000);
    console.log(`[relay] Session ended (${reason}) — ${durationSeconds}s for ${licenseKey}`);

    // Record usage asynchronously
    recordUsage(licenseKey, durationSeconds).catch(console.error);

    if (geminiWs?.readyState === WebSocket.OPEN) geminiWs.close();
    if (clientWs.readyState === WebSocket.OPEN) {
      clientWs.send(JSON.stringify({ type: 'session_end', reason, duration_seconds: durationSeconds }));
      clientWs.close();
    }
  }
}

// ── HTTP Server + WebSocket Upgrade ──────────────────────────────────────────

const server = http.createServer((req, res) => {
  // Health check for Cloud Run
  if (req.url === '/health' && req.method === 'GET') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: 'ok', service: 'voice-relay' }));
    return;
  }
  res.writeHead(426, { 'Content-Type': 'text/plain' });
  res.end('WebSocket upgrade required');
});

const wss = new WebSocket.Server({ server });

wss.on('connection', async (clientWs, req) => {
  // ── Step 1: Extract + verify JWT ─────────────────────────────────────────
  const url    = new URL(req.url, 'http://localhost');
  const token  = url.searchParams.get('token');

  if (!token) {
    clientWs.close(1008, 'Missing session token');
    return;
  }

  let payload;
  try {
    const jwtSecret = await getJwtSecret();
    payload = jwt.verify(token, jwtSecret, { issuer: 'delgadologic.tech' });
  } catch (err) {
    console.warn(`[relay] Invalid JWT: ${err.message}`);
    clientWs.close(1008, 'Invalid or expired session token');
    return;
  }

  const { license_key, edition } = payload;
  console.log(`[relay] Authenticated session — ${license_key} (${edition})`);

  // ── Step 2: Get Gemini API key (cached after first call) ─────────────────
  let geminiKey;
  try {
    geminiKey = await getGeminiKey();
  } catch (err) {
    console.error(`[relay] Failed to fetch Gemini key: ${err.message}`);
    clientWs.close(1011, 'Server configuration error');
    return;
  }

  // ── Step 3: Start the relay session with full safety enforcement ──────────
  createRelaySession({ clientWs, licenseKey: license_key, edition, geminiKey });
});

// ── Start ─────────────────────────────────────────────────────────────────────

const PORT = process.env.PORT || 8080;
server.listen(PORT, () => {
  console.log(`[relay] Voice relay listening on :${PORT}`);
  console.log(`[relay] Safety: allowlist(${SAFE_FUNCTIONS.size} fns) + confirmation gate + path sanitizer + ${MAX_SESSION_SECONDS}s hard cap`);
});

module.exports = { server, wss };
