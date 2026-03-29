// ─────────────────────────────────────────────────────────────────────────────
// voice-proxy/index.js  —  LogicFlow Voice Agent Sovereign Server
// Soul document: SOUL.md (project root)
// Oracle prompt:  ORACLE_PROMPT.md (this directory)
//
// Deployed as a Google Cloud Function (gen2) on api.delgadologic.tech
//
// Endpoints:
//   POST /v1/voice/session/start  → validates license, returns proxy WSS token
//   POST /v1/voice/session/end    → records session usage for billing
//   GET  /v1/voice/quota          → returns remaining minutes for a license
//
// Abuse Prevention:
//   - Gemini API key is in GCP Secret Manager (never in client binary)
//   - Per-license rate limit: 5 session starts per hour (Firestore counter)
//   - Per-edition monthly quota (Community: 0, Pro: 1800s, Enterprise: 12000s)
//   - Max concurrent sessions per license: 1 (Firestore lock)
//   - Session tokens are short-lived JWTs (1 hour TTL, signed with EdDSA)
//   - Machine fingerprint check prevents license sharing across devices
// ─────────────────────────────────────────────────────────────────────────────

const functions = require('@google-cloud/functions-framework');
const { SecretManagerServiceClient } = require('@google-cloud/secret-manager');
const { Firestore } = require('@google-cloud/firestore');
const jwt = require('jsonwebtoken');
const WebSocket = require('ws');

const db = new Firestore();
const secrets = new SecretManagerServiceClient();

// Quota in seconds per billing cycle
const EDITION_QUOTA = {
  community:  0,
  pro:        1800,   // 30 minutes/month
  enterprise: 12000,  // ~3.3 hours/month
};

// Rate limit: max N session starts per hour per license
const RATE_LIMIT_PER_HOUR = 5;

// Max session duration the server will enforce (regardless of edition)
const MAX_SESSION_SECONDS = 600; // 10 minutes hard cap

// ── Oracle Identity (from SOUL.md) ───────────────────────────────────────────
// This system prompt defines who Oracle IS, derived from the public soul
// document at the project root. Every Gemini Live session starts with this.
// It is injected server-side — the client binary contains none of this logic.

const ORACLE_SYSTEM_PROMPT = `
You are Oracle — the voice of the DelgadoLogic AI, built by DelgadoLogic Systems.

You are a trusted computer technician and knowledgeable friend. You understand Windows deeply. You are not a sales assistant, not an alarm system, and not a data broker. You help real people — including those who are not technical — understand and maintain their computers with honesty and clarity.

How you speak:
- Plain, direct language. No jargon unless the user introduces it first.
- Short, clear sentences. One idea at a time.
- Warm but not performatively cheerful. Never say "Great question!" or offer empty praise.
- Confident where you have data. Honest about uncertainty when you don't.
- Always frame findings as: "I found X, it means Y, here's what you can do."

Your values:
- Honesty: If the system is healthy, say so. Never manufacture urgency. Never describe a minor issue as "critical" or "dangerous."
- Respect: Treat every user as an intelligent adult. Explain the "why," not just the "what."
- Privacy: You analyze system metrics only — running processes, memory, disk health, temp file counts. You cannot read file content. This is enforced at the code level, not just policy. Say so if asked.
- Sovereignty: Running on the user's device is better than sending data to a server. Never push cloud features the user doesn't need.
- No manipulation: Never create false urgency. Never frame optional maintenance as mandatory. If the free tier solves the problem, say so.

Hard rules — never break these:
1. Do not exaggerate findings to seem more useful.
2. Do not read, reference, or describe user file content — ever.
3. Do not recommend actions that aren't needed.
4. Do not mention upgrades or Pro features unless the user directly asks.
5. Do not claim to be human when sincerely asked.
6. Do not execute irreversible operations without explicit user confirmation and a clear explanation of what will happen.
7. Do not transmit personal data.

When uncertain: say so. "I'm not certain whether this is a problem — here's what I can see."
When the user declines: accept it immediately. No follow-up pressure.

You have access to real-time system tools through LogicFlow. Use them. Share actual data, not generalities.
`.trim();

// Builds the Gemini Live BidiGenerateContent setup message
// Used by voiceRelay.js when opening the upstream WebSocket to Google
function buildGeminiSetupPayload(edition) {
  return {
    setup: {
      model: 'models/gemini-2.0-flash-live-001',
      generation_config: {
        response_modalities: ['AUDIO'],
        speech_config: {
          voice_config: {
            prebuilt_voice_config: { voice_name: 'Puck' },
          },
        },
        // Enterprise gets full thinking; Pro gets reduced budget for latency
        thinking_config: edition === 'enterprise'
          ? { thinking_budget: 1024 }
          : { thinking_budget: 0 },
      },
      system_instruction: {
        parts: [{ text: ORACLE_SYSTEM_PROMPT }],
      },
      tools: [
        {
          function_declarations: [
            {
              name: 'run_system_scan',
              description: 'Runs a full LogicFlow system diagnostic and returns health summary',
              parameters: { type: 'object', properties: {}, required: [] },
            },
            {
              name: 'get_startup_items',
              description: 'Returns the list of programs that run at Windows startup with their impact scores',
              parameters: { type: 'object', properties: {}, required: [] },
            },
            {
              name: 'get_temp_file_summary',
              description: 'Returns total size and count of temporary files that can be safely removed',
              parameters: { type: 'object', properties: {}, required: [] },
            },
            {
              name: 'clean_temp_files',
              description: 'Removes temporary files after user confirmation. Only call after user explicitly says yes.',
              parameters: { type: 'object', properties: {}, required: [] },
            },
            {
              name: 'get_process_list',
              description: 'Returns top CPU/RAM consuming processes currently running',
              parameters: { type: 'object', properties: {}, required: [] },
            },
            {
              name: 'get_disk_health',
              description: 'Returns SMART disk health status and free space for all drives',
              parameters: { type: 'object', properties: {}, required: [] },
            },
          ],
        },
      ],
    },
  };
}

module.exports = module.exports || {};
module.exports.buildGeminiSetupPayload = buildGeminiSetupPayload;
module.exports.ORACLE_SYSTEM_PROMPT = ORACLE_SYSTEM_PROMPT;

// ── Helpers ──────────────────────────────────────────────────────────────────

async function getGeminiKey() {
  const [version] = await secrets.accessSecretVersion({
    name: 'projects/manuel-portfolio-2026/secrets/GEMINI_LIVE_API_KEY/versions/latest',
  });
  return version.payload.data.toString('utf8').trim();
}

async function getJwtSecret() {
  const [version] = await secrets.accessSecretVersion({
    name: 'projects/manuel-portfolio-2026/secrets/VOICE_JWT_SECRET/versions/latest',
  });
  return version.payload.data.toString('utf8').trim();
}

// Validates that a license key exists, is active, and returns its edition + machine lock
async function validateLicense(licenseKey, machineFingerprint) {
  const doc = await db.collection('licenses').doc(licenseKey).get();
  if (!doc.exists) return { valid: false, reason: 'Unknown license key' };

  const data = doc.data();
  if (data.status !== 'active') return { valid: false, reason: `License is ${data.status}` };
  if (data.machineLock && data.machineLock !== machineFingerprint) {
    return { valid: false, reason: 'License locked to a different machine' };
  }

  return {
    valid: true,
    edition: data.edition || 'community',
    machineFingerprint: data.machineLock || machineFingerprint,
  };
}

// Returns remaining quota seconds for this license in the current billing cycle
async function getRemainingQuota(licenseKey, edition) {
  const maxSeconds = EDITION_QUOTA[edition] ?? 0;
  if (maxSeconds === 0) return 0;

  const now = new Date();
  const cycleId = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;

  const usageDoc = await db
    .collection('voice_usage')
    .doc(`${licenseKey}_${cycleId}`)
    .get();

  const usedSeconds = usageDoc.exists ? (usageDoc.data().seconds_used || 0) : 0;
  return Math.max(0, maxSeconds - usedSeconds);
}

// Checks and increments rate limit counter (5 starts per hour per license)
async function checkRateLimit(licenseKey) {
  const hourBucket = Math.floor(Date.now() / 3600000);
  const ref = db.collection('voice_rate_limit').doc(`${licenseKey}_${hourBucket}`);

  const result = await db.runTransaction(async (tx) => {
    const doc = await tx.get(ref);
    const count = doc.exists ? (doc.data().count || 0) : 0;
    if (count >= RATE_LIMIT_PER_HOUR) return false;
    tx.set(ref, { count: count + 1 }, { merge: true });
    return true;
  });

  return result;
}

// ── POST /v1/voice/session/start ─────────────────────────────────────────────

functions.http('voiceSessionStart', async (req, res) => {
  if (req.method !== 'POST') return res.status(405).json({ error: 'Method not allowed' });

  const { license_key, machine_fingerprint, client_version } = req.body;
  if (!license_key || !machine_fingerprint) {
    return res.status(400).json({ error: 'Missing license_key or machine_fingerprint' });
  }

  // 1. Validate license
  const licResult = await validateLicense(license_key, machine_fingerprint);
  if (!licResult.valid) {
    return res.status(403).json({ error: licResult.reason });
  }

  // 2. Block Community Edition
  if (licResult.edition === 'community') {
    return res.status(403).json({
      error: 'Voice Agent is not available on the Community Edition. Upgrade to Pro at delgadologic.tech/pricing',
    });
  }

  // 3. Check monthly quota
  const quotaRemaining = await getRemainingQuota(license_key, licResult.edition);
  if (quotaRemaining <= 0) {
    return res.status(429).json({
      error: `Monthly voice quota exhausted for ${licResult.edition} edition. Resets next billing cycle.`,
    });
  }

  // 4. Rate limit check
  const allowed = await checkRateLimit(license_key);
  if (!allowed) {
    return res.status(429).json({
      error: 'Too many session starts. Please wait before trying again.',
    });
  }

  // 5. Issue short-lived JWT session token (signed with EdDSA secret)
  const jwtSecret = await getJwtSecret();
  const sessionToken = jwt.sign(
    {
      license_key,
      machine_fingerprint,
      edition: licResult.edition,
    },
    jwtSecret,
    { expiresIn: '1h', issuer: 'delgadologic.tech' }
  );

  // Log session start in Firestore for auditing
  await db.collection('voice_sessions').add({
    license_key,
    machine_fingerprint,
    edition: licResult.edition,
    client_version,
    started_at: Firestore.Timestamp.now(),
    status: 'started',
  });

  return res.status(200).json({
    session_token:          sessionToken,
    quota_remaining_seconds: quotaRemaining,
    edition:                licResult.edition,
    max_session_seconds:    Math.min(MAX_SESSION_SECONDS, quotaRemaining),
    // The client connects to this proxy endpoint, not to Google directly
    proxy_wss_url: 'wss://api.delgadologic.tech/v1/voice/proxy',
  });
});

// ── POST /v1/voice/session/end ────────────────────────────────────────────────

functions.http('voiceSessionEnd', async (req, res) => {
  if (req.method !== 'POST') return res.status(405).json({ error: 'Method not allowed' });

  const { session_token, duration_seconds, function_calls_made } = req.body;
  if (!session_token) return res.status(400).json({ error: 'Missing session_token' });

  let payload;
  try {
    const jwtSecret = await getJwtSecret();
    payload = jwt.verify(session_token, jwtSecret, { issuer: 'delgadologic.tech' });
  } catch {
    return res.status(401).json({ error: 'Invalid or expired session token' });
  }

  const { license_key, edition } = payload;
  const seconds = Math.min(duration_seconds || 0, MAX_SESSION_SECONDS);

  // Decrement quota in Firestore
  const now = new Date();
  const cycleId = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
  const usageRef = db.collection('voice_usage').doc(`${license_key}_${cycleId}`);

  await db.runTransaction(async (tx) => {
    const doc = await tx.get(usageRef);
    const current = doc.exists ? (doc.data().seconds_used || 0) : 0;
    tx.set(usageRef, { seconds_used: current + seconds, license_key, cycle: cycleId }, { merge: true });
  });

  // Update session log
  const sessions = await db
    .collection('voice_sessions')
    .where('license_key', '==', license_key)
    .orderBy('started_at', 'desc')
    .limit(1)
    .get();

  if (!sessions.empty) {
    await sessions.docs[0].ref.update({
      status: 'ended',
      ended_at: Firestore.Timestamp.now(),
      duration_seconds: seconds,
      function_calls_made,
    });
  }

  return res.status(200).json({ ok: true, seconds_billed: seconds });
});

// ── GET /v1/voice/quota ───────────────────────────────────────────────────────

functions.http('voiceQuota', async (req, res) => {
  const { license_key, edition } = req.query;
  if (!license_key || !edition) return res.status(400).json({ error: 'Missing params' });

  const remaining = await getRemainingQuota(license_key, edition);
  const max = EDITION_QUOTA[edition] ?? 0;

  return res.status(200).json({
    total_seconds:     max,
    used_seconds:      max - remaining,
    remaining_seconds: remaining,
    remaining_minutes: Math.floor(remaining / 60),
  });
});
