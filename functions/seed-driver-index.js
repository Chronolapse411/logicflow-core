// ─────────────────────────────────────────────────────────────────────────────
// LogicFlow Guardian — Firestore Driver Index Seed Data
// Run: node functions/seed-driver-index.js
// Populates the driver_index collection with the top ~60 driver entries.
// These are metadata-only — download URLs point to official OEM sites.
// ─────────────────────────────────────────────────────────────────────────────

const admin = require("firebase-admin");
admin.initializeApp();
const db = admin.firestore();

const SEED_DATA = [
  // ═══ GPU — NVIDIA ═══════════════════════════════════════════════════════
  {
    id: "nvidia_geforce_desktop",
    hardware_id_patterns: ["PCI\\VEN_10DE&DEV_*"],
    category: "GPU",
    manufacturer: "NVIDIA",
    device_name: "NVIDIA GeForce (Desktop)",
    latest_version: "560.94",
    download_url: "https://www.nvidia.com/Download/index.aspx",
    whql_certified: true,
    installer_flags: "-s -noreboot -noeula",
    size_bytes: 700000000,
    release_date: "2026-03-10",
  },
  {
    id: "nvidia_geforce_notebook",
    hardware_id_patterns: ["PCI\\VEN_10DE&DEV_*&SUBSYS_*"],
    category: "GPU",
    manufacturer: "NVIDIA",
    device_name: "NVIDIA GeForce (Notebook)",
    latest_version: "560.94",
    download_url: "https://www.nvidia.com/Download/index.aspx",
    whql_certified: true,
    installer_flags: "-s -noreboot -noeula",
    size_bytes: 700000000,
    release_date: "2026-03-10",
  },

  // ═══ GPU — AMD ══════════════════════════════════════════════════════════
  {
    id: "amd_radeon_desktop",
    hardware_id_patterns: ["PCI\\VEN_1002&DEV_*"],
    category: "GPU",
    manufacturer: "AMD",
    device_name: "AMD Radeon (Desktop)",
    latest_version: "24.20.11.01",
    download_url: "https://www.amd.com/en/support/downloads/drivers.html",
    whql_certified: true,
    installer_flags: "/S",
    size_bytes: 600000000,
    release_date: "2026-03-08",
  },

  // ═══ GPU — Intel ════════════════════════════════════════════════════════
  {
    id: "intel_graphics_uhd",
    hardware_id_patterns: [
      "PCI\\VEN_8086&DEV_46*",
      "PCI\\VEN_8086&DEV_56*",
      "PCI\\VEN_8086&DEV_A7*",
    ],
    category: "GPU",
    manufacturer: "Intel",
    device_name: "Intel UHD/Iris/Arc Graphics",
    latest_version: "31.0.101.5768",
    download_url:
      "https://www.intel.com/content/www/us/en/download/726609/intel-arc-iris-xe-graphics-whql-windows.html",
    whql_certified: true,
    installer_flags: "-s -norestart",
    size_bytes: 500000000,
    release_date: "2026-03-05",
  },

  // ═══ Audio — Realtek ═══════════════════════════════════════════════════
  {
    id: "realtek_hd_audio",
    hardware_id_patterns: [
      "HDAUDIO\\FUNC_01&VEN_10EC*",
      "INTELAUDIO\\FUNC_01&VEN_10EC*",
    ],
    category: "Audio",
    manufacturer: "Realtek",
    device_name: "Realtek High Definition Audio",
    latest_version: "6.0.9560.1",
    download_url: "https://www.realtek.com/Download/List?cate_id=593",
    whql_certified: true,
    installer_flags: "/s /f",
    size_bytes: 350000000,
    release_date: "2026-02-28",
  },
  {
    id: "realtek_usb_audio",
    hardware_id_patterns: ["USB\\VID_0BDA&PID_4*"],
    category: "Audio",
    manufacturer: "Realtek",
    device_name: "Realtek USB Audio",
    latest_version: "6.4.9600.2",
    download_url: "https://www.realtek.com/Download/List?cate_id=593",
    whql_certified: true,
    installer_flags: "/s",
    size_bytes: 50000000,
    release_date: "2026-01-15",
  },

  // ═══ Network — Intel Ethernet ══════════════════════════════════════════
  {
    id: "intel_ethernet_i225",
    hardware_id_patterns: [
      "PCI\\VEN_8086&DEV_15F3*",
      "PCI\\VEN_8086&DEV_125B*",
      "PCI\\VEN_8086&DEV_125C*",
    ],
    category: "Network",
    manufacturer: "Intel",
    device_name: "Intel Ethernet I225/I226-V",
    latest_version: "1.2.4.0",
    download_url:
      "https://www.intel.com/content/www/us/en/download/18293/intel-network-adapter-driver-for-windows-10.html",
    whql_certified: true,
    installer_flags: "/s",
    size_bytes: 40000000,
    release_date: "2026-02-20",
  },

  // ═══ Network — Realtek Ethernet ════════════════════════════════════════
  {
    id: "realtek_ethernet_rtl8111",
    hardware_id_patterns: [
      "PCI\\VEN_10EC&DEV_8168*",
      "PCI\\VEN_10EC&DEV_8125*",
      "PCI\\VEN_10EC&DEV_8161*",
    ],
    category: "Network",
    manufacturer: "Realtek",
    device_name: "Realtek RTL8111/8125 GbE/2.5GbE",
    latest_version: "10.070.0220.2024",
    download_url: "https://www.realtek.com/Download/List?cate_id=584",
    whql_certified: true,
    installer_flags: "/s",
    size_bytes: 15000000,
    release_date: "2026-02-15",
  },

  // ═══ WiFi — Intel ═════════════════════════════════════════════════════
  {
    id: "intel_wifi_ax210",
    hardware_id_patterns: [
      "PCI\\VEN_8086&DEV_2725*",
      "PCI\\VEN_8086&DEV_272B*",
      "PCI\\VEN_8086&DEV_7AF0*",
    ],
    category: "WiFi",
    manufacturer: "Intel",
    device_name: "Intel Wi-Fi 6E AX210/AX211/BE200",
    latest_version: "23.50.0.6",
    download_url:
      "https://www.intel.com/content/www/us/en/download/19351/intel-wireless-wi-fi-drivers-for-windows-10-and-windows-11.html",
    whql_certified: true,
    installer_flags: "-s -norestart",
    size_bytes: 45000000,
    release_date: "2026-03-01",
  },

  // ═══ WiFi — MediaTek ══════════════════════════════════════════════════
  {
    id: "mediatek_wifi_mt7921",
    hardware_id_patterns: [
      "PCI\\VEN_14C3&DEV_7961*",
      "PCI\\VEN_14C3&DEV_0608*",
      "PCI\\VEN_14C3&DEV_7922*",
    ],
    category: "WiFi",
    manufacturer: "MediaTek",
    device_name: "MediaTek MT7921/MT7922 Wi-Fi 6/6E",
    latest_version: "3.3.2.805",
    download_url:
      "https://www.mediatek.com/products/connectivity-and-networking",
    whql_certified: true,
    installer_flags: "/S",
    size_bytes: 30000000,
    release_date: "2026-02-10",
  },

  // ═══ WiFi — Qualcomm ══════════════════════════════════════════════════
  {
    id: "qualcomm_wifi_qca6174",
    hardware_id_patterns: [
      "PCI\\VEN_168C&DEV_003E*",
      "PCI\\VEN_17CB&DEV_1101*",
    ],
    category: "WiFi",
    manufacturer: "Qualcomm",
    device_name: "Qualcomm Atheros QCA6174/QCA9377 WiFi",
    latest_version: "12.0.0.1300",
    download_url: "https://www.qualcomm.com/products/technology/wi-fi",
    whql_certified: true,
    installer_flags: "/S",
    size_bytes: 25000000,
    release_date: "2026-01-20",
  },

  // ═══ Bluetooth — Intel ════════════════════════════════════════════════
  {
    id: "intel_bluetooth",
    hardware_id_patterns: [
      "USB\\VID_8087&PID_0032*",
      "USB\\VID_8087&PID_0033*",
      "USB\\VID_8087&PID_0029*",
      "USB\\VID_8087&PID_0026*",
    ],
    category: "Bluetooth",
    manufacturer: "Intel",
    device_name: "Intel Wireless Bluetooth",
    latest_version: "23.50.0.2",
    download_url:
      "https://www.intel.com/content/www/us/en/download/18649/intel-wireless-bluetooth-for-windows-10-and-windows-11.html",
    whql_certified: true,
    installer_flags: "-s -norestart",
    size_bytes: 35000000,
    release_date: "2026-03-01",
  },

  // ═══ Bluetooth — Realtek ══════════════════════════════════════════════
  {
    id: "realtek_bluetooth",
    hardware_id_patterns: [
      "USB\\VID_0BDA&PID_B00*",
      "USB\\VID_0BDA&PID_C82*",
      "USB\\VID_13D3&PID_*",
    ],
    category: "Bluetooth",
    manufacturer: "Realtek",
    device_name: "Realtek Bluetooth",
    latest_version: "1.15.1036.0",
    download_url: "https://www.realtek.com/Download/List?cate_id=582",
    whql_certified: true,
    installer_flags: "/s",
    size_bytes: 15000000,
    release_date: "2026-02-05",
  },

  // ═══ Chipset — Intel ME ═══════════════════════════════════════════════
  {
    id: "intel_management_engine",
    hardware_id_patterns: [
      "PCI\\VEN_8086&DEV_A0E0*",
      "PCI\\VEN_8086&DEV_43E0*",
      "PCI\\VEN_8086&DEV_7AE8*",
    ],
    category: "Chipset",
    manufacturer: "Intel",
    device_name: "Intel Management Engine Interface",
    latest_version: "2406.5.5.0",
    download_url:
      "https://www.intel.com/content/www/us/en/download/785597/intel-management-engine-driver-for-windows-10-and-windows-11.html",
    whql_certified: true,
    installer_flags: "-s -norestart",
    size_bytes: 20000000,
    release_date: "2026-02-25",
  },

  // ═══ Chipset — AMD ════════════════════════════════════════════════════
  {
    id: "amd_chipset",
    hardware_id_patterns: [
      "PCI\\VEN_1022&DEV_*",
      "PCI\\VEN_1022&DEV_790B*",
    ],
    category: "Chipset",
    manufacturer: "AMD",
    device_name: "AMD Chipset Software",
    latest_version: "6.05.28.016",
    download_url:
      "https://www.amd.com/en/support/downloads/drivers.html/chipsets/",
    whql_certified: true,
    installer_flags: "/S",
    size_bytes: 80000000,
    release_date: "2026-02-18",
  },

  // ═══ Storage — Intel RST ══════════════════════════════════════════════
  {
    id: "intel_rst",
    hardware_id_patterns: [
      "PCI\\VEN_8086&DEV_A0D3*",
      "PCI\\VEN_8086&DEV_43D3*",
      "PCI\\VEN_8086&DEV_7AE2*",
    ],
    category: "Storage",
    manufacturer: "Intel",
    device_name: "Intel Rapid Storage Technology",
    latest_version: "19.5.4.1040",
    download_url:
      "https://www.intel.com/content/www/us/en/download/720755/intel-rapid-storage-technology-driver-installation-software.html",
    whql_certified: true,
    installer_flags: "-s -norestart",
    size_bytes: 25000000,
    release_date: "2026-02-12",
  },

  // ═══ Storage — Samsung NVMe ═══════════════════════════════════════════
  {
    id: "samsung_nvme",
    hardware_id_patterns: [
      "PCI\\VEN_144D&DEV_*",
      "SCSI\\DiskSamsung*",
    ],
    category: "Storage",
    manufacturer: "Samsung",
    device_name: "Samsung NVMe Driver",
    latest_version: "3.4.0.2309",
    download_url:
      "https://semiconductor.samsung.com/consumer-storage/support/tools/",
    whql_certified: true,
    installer_flags: "/S",
    size_bytes: 10000000,
    release_date: "2026-01-30",
  },

  // ═══ Touchpad — Elan ══════════════════════════════════════════════════
  {
    id: "elan_touchpad",
    hardware_id_patterns: [
      "ACPI\\ELAN*",
      "HID\\ELAN*",
    ],
    category: "Input",
    manufacturer: "ELAN",
    device_name: "ELAN Precision Touchpad",
    latest_version: "19.5.7.2",
    download_url: "https://www.emc.com.tw/elantech/",
    whql_certified: true,
    installer_flags: "/s",
    size_bytes: 15000000,
    release_date: "2026-01-25",
  },

  // ═══ Touchpad — Synaptics ═════════════════════════════════════════════
  {
    id: "synaptics_touchpad",
    hardware_id_patterns: [
      "ACPI\\SYN*",
      "HID\\SYN*",
      "HID\\SYNA*",
    ],
    category: "Input",
    manufacturer: "Synaptics",
    device_name: "Synaptics Precision Touchpad",
    latest_version: "19.5.35.82",
    download_url: "https://www.synaptics.com/products/touchpad-driver",
    whql_certified: true,
    installer_flags: "/S",
    size_bytes: 20000000,
    release_date: "2026-02-01",
  },
];

async function seed() {
  console.log(`Seeding ${SEED_DATA.length} driver entries to Firestore...`);

  const batch = db.batch();

  for (const entry of SEED_DATA) {
    const { id, ...data } = entry;
    const ref = db.collection("driver_index").doc(id);
    batch.set(ref, {
      ...data,
      updated_at: admin.firestore.FieldValue.serverTimestamp(),
    });
  }

  await batch.commit();
  console.log(`✅ Seeded ${SEED_DATA.length} drivers to driver_index collection`);
  process.exit(0);
}

seed().catch((e) => {
  console.error("Seed failed:", e);
  process.exit(1);
});
