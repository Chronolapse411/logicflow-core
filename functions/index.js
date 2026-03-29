// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow Guardian — Firebase Cloud Functions
// Smart Driver Updater Backend: Firestore Lookup + Gemini AI Analysis
// ─────────────────────────────────────────────────────────────────────────────

const { onRequest, onSchedule } = require("firebase-functions/v2/https");
const { onSchedule: onCron } = require("firebase-functions/v2/scheduler");
const admin = require("firebase-admin");
const { VertexAI } = require("@google-cloud/vertexai");

admin.initializeApp();
const db = admin.firestore();

// ─── Configuration ──────────────────────────────────────────────────────
const PROJECT_ID = process.env.GCLOUD_PROJECT || "logicflow-guardian";
const LOCATION = "us-central1";

// ═══════════════════════════════════════════════════════════════════════════
//  CLOUD FUNCTION: driversLookup
//  Matches hardware IDs against Firestore driver_index collection.
//  Returns available driver updates with OEM download URLs.
// ═══════════════════════════════════════════════════════════════════════════

exports.driversLookup = onRequest(
  { cors: true, region: LOCATION, memory: "256MiB" },
  async (req, res) => {
    try {
      if (req.method !== "POST") {
        return res.status(405).json({ error: "POST required" });
      }

      const { drivers } = req.body;
      if (!Array.isArray(drivers) || drivers.length === 0) {
        return res.status(400).json({ error: "drivers array required" });
      }

      // Load the entire driver index (small — ~100 docs)
      const indexSnap = await db.collection("driver_index").get();
      const index = [];
      indexSnap.forEach((doc) => index.push({ id: doc.id, ...doc.data() }));

      const results = [];

      for (const device of drivers) {
        if (!device.HardwareId) continue;
        const hwid = device.HardwareId.toUpperCase();

        // Match against hardware_id_patterns in the index
        for (const entry of index) {
          const patterns = entry.hardware_id_patterns || [];
          const matched = patterns.some((pattern) => {
            // Support wildcard matching: "PCI\\VEN_10DE&DEV_*"
            const regex = new RegExp(
              "^" + pattern.replace(/\\/g, "\\\\").replace(/\*/g, ".*") + "$",
              "i"
            );
            return regex.test(hwid);
          });

          if (!matched) continue;

          // Check if index has a newer version
          const currentVersion = device.DriverVersion || "0.0.0.0";
          const indexVersion = entry.latest_version || "";

          if (
            indexVersion &&
            compareVersions(indexVersion, currentVersion) > 0
          ) {
            results.push({
              HardwareId: device.HardwareId,
              DeviceName: device.DeviceName || entry.device_name || "",
              CurrentVersion: currentVersion,
              LatestVersion: indexVersion,
              DownloadUrl: entry.download_url || "",
              SizeBytes: entry.size_bytes || 0,
              IsWhqlCertified: entry.whql_certified || false,
              Manufacturer: entry.manufacturer || "",
              ReleasedAt: entry.release_date || "",
              Source: "FirestoreIndex",
              InstallerFlags: entry.installer_flags || "",
              Category: entry.category || "Other",
            });
          }
          break; // One match per device
        }
      }

      console.log(
        `driversLookup: ${drivers.length} devices → ${results.length} updates`
      );
      return res.json(results);
    } catch (error) {
      console.error("driversLookup error:", error);
      return res.status(500).json({ error: "Internal server error" });
    }
  }
);

// ═══════════════════════════════════════════════════════════════════════════
//  CLOUD FUNCTION: driversScanWithAi
//  Combines Firestore lookup with Gemini AI crash-to-driver analysis.
//  Input: device list + crash digest from Pulse telemetry
//  Output: AI-scored driver recommendations with severity & confidence
// ═══════════════════════════════════════════════════════════════════════════

exports.driversScanWithAi = onRequest(
  { cors: true, region: LOCATION, memory: "512MiB", timeoutSeconds: 60 },
  async (req, res) => {
    try {
      if (req.method !== "POST") {
        return res.status(405).json({ error: "POST required" });
      }

      const { devices, crashDigest, systemInfo } = req.body;
      if (!devices || !crashDigest) {
        return res
          .status(400)
          .json({ error: "devices and crashDigest required" });
      }

      // Initialize Vertex AI / Gemini
      const vertexAi = new VertexAI({ project: PROJECT_ID, location: LOCATION });
      const model = vertexAi.getGenerativeModel({ model: "gemini-2.0-flash" });

      // Build the AI prompt
      const prompt = buildDriverAnalysisPrompt(
        devices,
        crashDigest,
        systemInfo
      );

      // Call Gemini
      const result = await model.generateContent(prompt);
      const response = result.response;
      const text =
        response.candidates?.[0]?.content?.parts?.[0]?.text || "[]";

      // Parse AI response (expecting JSON array of recommendations)
      let recommendations = [];
      try {
        // Extract JSON from the response (Gemini might wrap it in markdown)
        const jsonMatch = text.match(/\[[\s\S]*\]/);
        if (jsonMatch) {
          recommendations = JSON.parse(jsonMatch[0]);
        }
      } catch (parseError) {
        console.warn("Failed to parse AI response as JSON:", parseError);
        console.log("Raw AI response:", text);
      }

      // Normalize the AI output to match our model
      const normalized = recommendations.map((rec) => ({
        DriverName: rec.driverName || rec.DriverName || "",
        HardwareId: rec.hardwareId || rec.HardwareId || "",
        Reason: rec.reason || rec.Reason || "",
        Confidence: parseFloat(rec.confidence || rec.Confidence || 0.5),
        Severity: rec.severity || rec.Severity || "recommended",
        CrashSignatures: rec.crashSignatures || rec.CrashSignatures || [],
        CrashCount: parseInt(rec.crashCount || rec.CrashCount || 0),
        SuggestedUpdate: rec.suggestedUpdate || rec.SuggestedUpdate || null,
      }));

      // Store the analysis in Firestore for future reference
      await db.collection("driver_ai_analyses").add({
        timestamp: admin.firestore.FieldValue.serverTimestamp(),
        deviceCount: devices.length,
        recommendationCount: normalized.length,
        systemInfo: systemInfo || {},
      });

      console.log(
        `driversScanWithAi: ${devices.length} devices, ${normalized.length} AI recommendations`
      );
      return res.json(normalized);
    } catch (error) {
      console.error("driversScanWithAi error:", error);
      return res.status(500).json({ error: "AI analysis failed" });
    }
  }
);

// ═══════════════════════════════════════════════════════════════════════════
//  CLOUD FUNCTION: driversScraperWeekly
//  Scheduled weekly — scrapes OEM download pages for latest driver versions.
//  Updates the Firestore driver_index collection.
// ═══════════════════════════════════════════════════════════════════════════

exports.driversScraperWeekly = onCron(
  { schedule: "every sunday 02:00", region: LOCATION, memory: "512MiB", timeoutSeconds: 300 },
  async (context) => {
    console.log("Weekly driver index scraper starting...");

    const updates = [];

    // ── NVIDIA GeForce ──────────────────────────────────────────────
    try {
      const nvidiaVersion = await scrapeNvidiaLatest();
      if (nvidiaVersion) {
        updates.push({
          id: "nvidia_geforce_desktop",
          data: {
            latest_version: nvidiaVersion.version,
            download_url: nvidiaVersion.url,
            release_date: new Date().toISOString(),
            updated_at: admin.firestore.FieldValue.serverTimestamp(),
          },
        });
      }
    } catch (e) {
      console.warn("NVIDIA scrape failed:", e.message);
    }

    // ── AMD Radeon ──────────────────────────────────────────────────
    try {
      const amdVersion = await scrapeAmdLatest();
      if (amdVersion) {
        updates.push({
          id: "amd_radeon_desktop",
          data: {
            latest_version: amdVersion.version,
            download_url: amdVersion.url,
            release_date: new Date().toISOString(),
            updated_at: admin.firestore.FieldValue.serverTimestamp(),
          },
        });
      }
    } catch (e) {
      console.warn("AMD scrape failed:", e.message);
    }

    // ── Intel Graphics ──────────────────────────────────────────────
    try {
      const intelVersion = await scrapeIntelLatest();
      if (intelVersion) {
        updates.push({
          id: "intel_graphics_desktop",
          data: {
            latest_version: intelVersion.version,
            download_url: intelVersion.url,
            release_date: new Date().toISOString(),
            updated_at: admin.firestore.FieldValue.serverTimestamp(),
          },
        });
      }
    } catch (e) {
      console.warn("Intel scrape failed:", e.message);
    }

    // Apply all updates to Firestore
    const batch = db.batch();
    for (const update of updates) {
      const ref = db.collection("driver_index").doc(update.id);
      batch.set(ref, update.data, { merge: true });
    }
    await batch.commit();

    console.log(
      `Weekly scraper complete: ${updates.length} drivers updated in index`
    );
  }
);

// ═══════════════════════════════════════════════════════════════════════════
//  CLOUD FUNCTION: driversIngestTelemetry
//  Processes crowd-sourced driver fingerprints from Pulse reports.
//  Builds aggregate intelligence:
//    1. Tracks hardware ID popularity (what hardware is common?)
//    2. Tracks driver version distribution per hardware ID
//    3. Discovers new hardware IDs not yet in curated index
//    4. Auto-suggests new index entries when hwid seen 10+ times
//    5. Correlates crash reports with specific driver versions
// ═══════════════════════════════════════════════════════════════════════════

exports.driversIngestTelemetry = onRequest(
  { cors: true, region: LOCATION, memory: "256MiB" },
  async (req, res) => {
    try {
      if (req.method !== "POST") {
        return res.status(405).json({ error: "POST required" });
      }

      const { fingerprint, installId, eventType, crashData } = req.body;
      if (!fingerprint || !fingerprint.KeyDrivers) {
        return res
          .status(400)
          .json({ error: "fingerprint with KeyDrivers required" });
      }

      const batch = db.batch();
      const now = admin.firestore.FieldValue.serverTimestamp();
      const isCrash = eventType === "crash";

      // ── Process each key driver ──────────────────────────────────────
      for (const driver of fingerprint.KeyDrivers) {
        if (!driver.HwId) continue;

        // Normalize HW ID for consistent lookups
        const hwid = driver.HwId.toUpperCase().trim();
        const docId = hwid.replace(/[\\/#]/g, "_").substring(0, 128);

        const telemetryRef = db.collection("driver_telemetry").doc(docId);

        // Increment hardware popularity + version counts atomically
        const safeVersion = (driver.Version || "unknown").replace(/\./g, "_");

        batch.set(
          telemetryRef,
          {
            hardwareId: hwid,
            deviceClass: driver.Class || "",
            lastProvider: driver.Provider || "",
            lastSeen: now,
            hitCount: admin.firestore.FieldValue.increment(1),
            [`versions.${safeVersion}`]:
              admin.firestore.FieldValue.increment(1),
            // Track crash association
            ...(isCrash && {
              crashCount: admin.firestore.FieldValue.increment(1),
              lastCrashAt: now,
            }),
          },
          { merge: true }
        );
      }

      // ── Track firmware telemetry ───────────────────────────────────
      if (fingerprint.FirmwareVersion) {
        const fwRef = db
          .collection("firmware_telemetry")
          .doc(
            (fingerprint.FirmwareManufacturer || "unknown")
              .replace(/\s+/g, "_")
              .substring(0, 64)
          );

        batch.set(
          fwRef,
          {
            manufacturer: fingerprint.FirmwareManufacturer || "",
            lastVersion: fingerprint.FirmwareVersion,
            lastSeen: now,
            hitCount: admin.firestore.FieldValue.increment(1),
          },
          { merge: true }
        );
      }

      await batch.commit();

      // ── Check for auto-suggest candidates (async, non-blocking) ────
      checkAutoSuggestCandidates().catch((e) =>
        console.warn("Auto-suggest check failed:", e.message)
      );

      console.log(
        `driversIngestTelemetry: ${fingerprint.KeyDrivers.length} drivers, ` +
          `install=${installId || "anon"}, crash=${isCrash}`
      );

      return res.json({
        accepted: true,
        driversProcessed: fingerprint.KeyDrivers.length,
      });
    } catch (error) {
      console.error("driversIngestTelemetry error:", error);
      return res.status(500).json({ error: "Telemetry ingestion failed" });
    }
  }
);

/**
 * Checks driver_telemetry for hardware IDs seen 10+ times that
 * are NOT in the curated driver_index. Logs them as suggestions.
 */
async function checkAutoSuggestCandidates() {
  const telemetrySnap = await db
    .collection("driver_telemetry")
    .where("hitCount", ">=", 10)
    .limit(50)
    .get();

  if (telemetrySnap.empty) return;

  const indexSnap = await db.collection("driver_index").get();
  const indexedPatterns = new Set();
  indexSnap.forEach((doc) => {
    const patterns = doc.data().hardware_id_patterns || [];
    patterns.forEach((p) => indexedPatterns.add(p.toUpperCase()));
  });

  for (const doc of telemetrySnap.docs) {
    const data = doc.data();
    const hwid = data.hardwareId || "";

    // Check if any index pattern already matches
    let alreadyIndexed = false;
    for (const pattern of indexedPatterns) {
      const regex = new RegExp(
        "^" + pattern.replace(/\\/g, "\\\\").replace(/\*/g, ".*") + "$",
        "i"
      );
      if (regex.test(hwid)) {
        alreadyIndexed = true;
        break;
      }
    }

    if (!alreadyIndexed) {
      await db
        .collection("driver_index_suggestions")
        .doc(doc.id)
        .set(
          {
            hardwareId: hwid,
            deviceClass: data.deviceClass || "",
            lastProvider: data.lastProvider || "",
            hitCount: data.hitCount || 0,
            crashCount: data.crashCount || 0,
            versions: data.versions || {},
            suggestedAt: admin.firestore.FieldValue.serverTimestamp(),
            status: "pending", // pending | approved | rejected
          },
          { merge: true }
        );

      console.log(
        `Auto-suggest: ${hwid} (${data.hitCount} hits, ${data.crashCount || 0} crashes)`
      );
    }
  }
}

// ═══════════════════════════════════════════════════════════════════════════
//  HELPER: Build Gemini AI Prompt
// ═══════════════════════════════════════════════════════════════════════════

function buildDriverAnalysisPrompt(devices, crashDigest, systemInfo) {
  return `You are an expert Windows system diagnostician analyzing driver health.

TASK: Analyze the following system crash data and installed drivers. Identify which
drivers are likely causing system instability. Return ONLY a JSON array of recommendations.

SYSTEM INFO:
${JSON.stringify(systemInfo, null, 2)}

INSTALLED DRIVERS (${devices.length} total):
${JSON.stringify(
  devices.map((d) => ({
    name: d.DeviceName,
    hwid: d.HardwareId,
    version: d.DriverVersion,
    class: d.DeviceClass,
    date: d.DriverDate,
  })),
  null,
  2
)}

CRASH/ERROR DIGEST:
${typeof crashDigest === "string" ? crashDigest : JSON.stringify(crashDigest, null, 2)}

KNOWN CRASH-DRIVER CORRELATIONS:
- nvlddmkm.sys → NVIDIA GPU driver
- atikmdag.sys / atikmpag.sys → AMD GPU driver
- igdkmd64.sys → Intel GPU driver
- RTKVHD64.sys → Realtek Audio driver
- e1d65x64.sys → Intel Ethernet driver
- Netwtw10.sys → Intel WiFi driver
- storport.sys / stornvme.sys → Storage controller driver
- NTFS.sys I/O errors → Possible storage driver issue

RESPONSE FORMAT: Return ONLY a valid JSON array (no markdown, no explanation), where each
element has these fields:
- "driverName": string (device display name)
- "hardwareId": string (matching hardware ID from installed drivers, or empty)
- "reason": string (human-readable explanation of why this driver needs attention)
- "confidence": number (0.0 to 1.0, how confident the correlation is)
- "severity": string ("critical" if causing crashes, "recommended" if outdated, "optional" if minor)
- "crashSignatures": string[] (crash module names that triggered this recommendation)
- "crashCount": number (estimated crash count related to this driver)

If no driver issues are found, return an empty array: []

JSON ARRAY:`;
}

// ═══════════════════════════════════════════════════════════════════════════
//  HELPER: OEM Scraper Functions
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Scrapes latest NVIDIA GeForce driver version.
 * Uses NVIDIA's lookup API for Game Ready drivers.
 */
async function scrapeNvidiaLatest() {
  try {
    // NVIDIA provides a lookup API for driver versions
    // pfid=915 = GeForce GTX/RTX desktop, osID=57 = Windows 10/11 64-bit
    const url =
      "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=1";
    const response = await fetch(url);
    if (!response.ok) return null;

    const text = await response.text();
    // Parse XML response for latest version
    const versionMatch = text.match(/<Version>([\d.]+)<\/Version>/);
    const urlMatch = text.match(/<DownloadURL>([^<]+)<\/DownloadURL>/);

    if (versionMatch) {
      return {
        version: versionMatch[1],
        url: urlMatch ? urlMatch[1] : `https://www.nvidia.com/Download/index.aspx`,
      };
    }
  } catch (e) {
    console.warn("NVIDIA API scrape error:", e.message);
  }
  return null;
}

/**
 * Scrapes latest AMD Radeon driver version.
 */
async function scrapeAmdLatest() {
  try {
    // AMD provides release notes pages that can be parsed
    const url = "https://www.amd.com/en/support/downloads/drivers.html";
    const response = await fetch(url);
    if (!response.ok) return null;

    const text = await response.text();
    // Look for Adrenalin version pattern
    const versionMatch = text.match(
      /Adrenalin[\s\S]*?(\d+\.\d+\.\d+)/i
    );

    if (versionMatch) {
      return {
        version: versionMatch[1],
        url: "https://www.amd.com/en/support/downloads/drivers.html",
      };
    }
  } catch (e) {
    console.warn("AMD scrape error:", e.message);
  }
  return null;
}

/**
 * Scrapes latest Intel Graphics driver version.
 */
async function scrapeIntelLatest() {
  try {
    const url =
      "https://downloadcenter.intel.com/product/80939/Graphics";
    const response = await fetch(url);
    if (!response.ok) return null;

    const text = await response.text();
    const versionMatch = text.match(
      /Intel.*?Graphics.*?Driver.*?(\d+\.\d+\.\d+\.\d+)/i
    );

    if (versionMatch) {
      return {
        version: versionMatch[1],
        url: "https://www.intel.com/content/www/us/en/download-center/home.html",
      };
    }
  } catch (e) {
    console.warn("Intel scrape error:", e.message);
  }
  return null;
}

// ═══════════════════════════════════════════════════════════════════════════
//  HELPER: Version Comparison
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Compares two version strings. Returns > 0 if a > b, < 0 if a < b, 0 if equal.
 */
function compareVersions(a, b) {
  const partsA = a.split(".").map(Number);
  const partsB = b.split(".").map(Number);
  const len = Math.max(partsA.length, partsB.length);

  for (let i = 0; i < len; i++) {
    const numA = partsA[i] || 0;
    const numB = partsB[i] || 0;
    if (numA !== numB) return numA - numB;
  }
  return 0;
}
