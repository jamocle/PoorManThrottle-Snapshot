/*
  (C) James Theimer 2026 Poor Man's Throttle
  ESP32 BLE Heavy-Train Throttle Controller

  LED behavior (GPIO2):
  - Default / disconnected: LED blinks continuously (double-blink search pattern)
  - Connected: LED stays solid ON
  - RX/TX while connected (solid ON): LED briefly turns OFF (a quick dip), then returns solid ON

  Target: ESP32-WROOM-32 (Arduino framework)
  Motor driver: IBT-2 / BTS7960 (RPWM/LPWM)
  BLE: Custom service with RX (Write / WriteNR) + TX (Notify)

    ------------------------- BLE COMMAND REFERENCE -------------------------

  Notes:
  - Commands are case-insensitive.
  - Whitespace + CR/LF are trimmed.
  - Throttle values are clamped to 0..100.
  - Stop-first reversing is enforced (ramps to 0, waits direction-delay, then reverses).
  - After BLE disconnect, a grace timer runs. If not reconnected,
    a forced stop is latched and a stop ramp is executed.

  Response behavior:
  - Most commands respond with:
      ACK:<original command>   (valid)
      ERR:<original command>   (invalid)
  - The following commands return RAW text (no ACK/ERR wrapper):
      ?, ??, G, T, M, C, K, V, I

  --------------------------------------------------------------------------

  MOTION (Momentum / Nonlinear Ramp - smoothstep easing)
  - F<n>        : Forward to throttle n (0..100) using momentum ramp
  - R<n>        : Reverse to throttle n (0..100) using momentum ramp

  --------------------------------------------------------------------------

  MOTION (Quick Ramp - smoothstep, faster profile)
  - FQ<n>       : Forward to throttle n (0..100) using quick ramp
                  If reversing, performs stop ramp → direction delay → quick ramp up
  - RQ<n>       : Reverse to throttle n (0..100) using quick ramp

  --------------------------------------------------------------------------

  STOPS
  - S           : Quick stop ramp (ramps down to MINSTART floor (if set), then snaps to true STOP)
  - B           : Brake ramp (ramps down to MINSTART floor (if set), then snaps to true STOP)

  --------------------------------------------------------------------------

  FLOOR + CEILING REMAP (HW output shaping)

  M<n> = MINSTART FLOOR (HW floor remap)
  - M           : Query current hardware floor (RAW reply: M<n>)
  - M<n>        : Set hardware floor for nonzero throttles (0..100)
                  When M>0:
                    - Commanded throttle (mapped) is 1..100
                    - Hardware throttle (actual) is remapped into [M .. CEILING]
                      (If CEILING is not set, it defaults to 100.)
                      Example M20, C100 (default ceiling):
                        F1   -> HW 20
                        F100 -> HW 100

  C<n> = CEILING (HW ceiling remap)
  - C           : Query current effective hardware ceiling (RAW reply: C<n>)
                  (If stored ceiling is 0/disabled, effective ceiling is 100.)
  - C<n>        : Set hardware ceiling cap for nonzero throttles (0..100)
                  When C is between 1..99:
                    - Commanded throttle (mapped) is 1..100
                    - Hardware throttle (actual) is remapped into [FLOOR .. C]
                      (If FLOOR is not set, it defaults to 0.)
                      Example M0, C60:
                        F1   -> HW 1 (approximately; near-low values track closely)
                        F100 -> HW 60 (capped)
                      Example M20, C60:
                        F1   -> HW 20
                        F100 -> HW 60

  Combined behavior:
    - Nonzero mapped values (1..100) map linearly into HW [floor..ceiling].
    - If ceiling is set below floor, ceiling is clamped up to floor.
    - If floor == ceiling, any nonzero mapped becomes exactly that HW value.

  Stops:
    - S/B ramps down to mapped=1 (HW floor), then snaps to STOP (0), when FLOOR > 0.

  --------------------------------------------------------------------------

  START ASSIST CONFIG (KICK)

  - K           : Query current kick config (RAW reply: K<t>,<ms>,<rampDownMs>,<maxApply>)
  - K<t>,<ms>
  - K<t>,<ms>,<rampDownMs>,<maxApply>

                  t          = kick throttle (MAPPED 0..100)
                  ms         = kick duration
                  rampDownMs = kick ramp-down duration (optional)
                  maxApply   = only kick if target (MAPPED) ≤ maxApply (optional)

                  When starting from stop and target > 0:
                  - Applies kick throttle for <ms> (then transitions into ramp behavior)
                  - FLOOR/CEILING remap still applies to hardware output.

  --------------------------------------------------------------------------

  TRAIN NAME (PERSISTED)

  - T           : Returns stored train name (RAW notify, no ACK wrapper)
  - T,<name>    : Sets stored train name (RAW notify confirmation: replies with name)
                  Allowed characters: A-Z, a-z, 0-9, space
                  Leading/trailing spaces are not stored
                  Max length is limited so the reply (including '\n') fits in one BLE notify

  --------------------------------------------------------------------------

  PERSISTENCE MAINTENANCE

  - X           : Wipes all persisted config and reboots
                  (Typically responds ACK:X, then reboots)

  --------------------------------------------------------------------------

  DEBUG / DIAGNOSTICS

  - D1          : Debug ON (Serial @115200)
                  Prints FW name/version, BLE MTU, events, HW comparisons

  - D0          : Debug OFF

  - P0          : Periodic debug prints ONLY when hardware mismatch (default)

  - P1          : Periodic debug prints every period (even if matching)
  - I           : Query cached eFuse MAC (RAW reply: I:<hex>)

  --------------------------------------------------------------------------

  STATE QUERIES (RAW notify, no ACK wrapper)

  - ?           : Hardware state query
                  Returns TWO values: mapped-equivalent + actual hardware
                  Example:
                      HW-FORWARD M40 HW60
                      HW-STOPPED M0 HW0

  - ??          : Stored/applied state query
                  Returns TWO values: mapped + expected hardware (after remap)
                  Example:
                      FORWARD M40 HW60
                      STOPPED M0 HW0

  - V           : Version query
                  Responds as:
                      <FW_VERSION>
*/
#include <Arduino.h>
#include <NimBLEDevice.h>
#include <type_traits>
#include <stdarg.h>
#include <Preferences.h>

// ------------------------- Startup defaults -------------------------
static const bool DEBUG_AT_STARTUP = false;

// ------------------------- Cached chip ID (eFuse MAC) -------------------------
static String gEfuseHex = "";

// ------------------------- Firmware ID -------------------------
static const char* FW_NAME    = "GScaleThrottle";
static const char* FW_VERSION = "1.6.2";

// ------------------------- BLE UUIDs (custom) -------------------------
static const char* SERVICE_UUID = "9b2b7b30-5f3d-4a51-9bd6-1e8cde2c9000";
static const char* RX_UUID      = "9b2b7b31-5f3d-4a51-9bd6-1e8cde2c9000"; // Write / WriteNR
static const char* TX_UUID      = "9b2b7b32-5f3d-4a51-9bd6-1e8cde2c9000"; // Notify

// ------------------------- Strict no-float guard (compile-time) -------------------------
#ifndef DISABLE_STRICT_NO_FLOAT_GUARD
  #define float  __FORBIDDEN_FLOAT_TYPE_USE_INTEGER_FIXED_POINT__
  #define double __FORBIDDEN_DOUBLE_TYPE_USE_INTEGER_FIXED_POINT__
#endif

// ------------------------- State enums (MUST be before use) -------------------------
enum class Direction : uint8_t { STOP = 0, FWD = 1, REV = 2 };
enum class RampKind  : uint8_t { NONE = 0, MOMENTUM, BRAKE, QUICKSTOP };
enum class PendingStage : uint8_t { NONE = 0, WAIT_DIR_DELAY };

// ------------------------- Hardware readback (debug-only) -------------------------
struct HwSnapshot {
  bool ren;
  bool len;
  bool enabled;

  uint32_t dutyR;
  uint32_t dutyL;

  Direction hwDir;
  int32_t hwThrottlePct;   
};

// ------------------------- Pins & PWM -------------------------
static const int PIN_REN = 27;    // IBT-2 R_EN
static const int PIN_LPWM = 26;   // Reverse PWM -> LPWM
static const int PIN_RPWM = 25;   // Forward PWM -> RPWM
static const int PIN_LEN = 33;    // IBT-2 L_EN
static const uint32_t PWM_FREQ_HZ  = 20000;   // ~20kHz
static const uint8_t  PWM_RES_BITS = 10;      // 8–10 bits allowed
static const uint8_t  PWM_CH_R     = 0;
static const uint8_t  PWM_CH_L     = 1;
static const uint32_t PWM_MAX_DUTY = (1UL << PWM_RES_BITS) - 1;

// ------------------------- PWM State ---------------------------
static bool pwmInitialized = false;

// ------------------------- Fixed-point easing -------------------------
static const int32_t P_SCALE = 1000; 

// ------------------------- Timing constants -------------------------
static const uint32_t FULL_MOMENTUM_ACCEL_MS = 30000; // full-scale 100 step
static const uint32_t FULL_MOMENTUM_DECEL_MS = 35000;
static const uint32_t FULL_QUICKRAMP_ACCEL_MS = 2500; 
static const uint32_t FULL_QUICKRAMP_DECEL_MS = 2500;
static const uint32_t FULL_QUICKSTOP_MS      = 3000;
static const uint32_t FULL_BRAKE_MS          = 15000;
static const uint32_t DIR_CHANGE_DELAY_MS    = 2000;
static const uint32_t BLE_GRACE_MS           = 15000;
static const uint32_t GRACE_COUNTDOWN_LOG_PERIOD_MS = 1000; // log in debug
static const uint32_t DEBUG_HW_SNAPSHOT_PERIOD_MS = 2000;
static const bool DEBUG_PRINT_PERIODIC_ONLY_ON_MISMATCH = true;
static const bool DEBUG_PRINT_STORED_ONLY_ON_MISMATCH = false;

// ------------------------- Persistence (NVS / Preferences) -------------------------
static const char* NVS_NAMESPACE = "pmt";
static const uint32_t NVS_SCHEMA_VERSION = 1;
static const uint32_t CFG_SAVE_DEBOUNCE_MS = 1000;

static bool cfgDirty = false;
static uint32_t cfgDirtySinceMs = 0;

// Pending reverse needs to remember which ramp constants to use after the delay
static uint32_t pendingFullAccelMs = FULL_MOMENTUM_ACCEL_MS;
static uint32_t pendingFullDecelMs = FULL_MOMENTUM_DECEL_MS;

static bool pendingSkipDirDelay = false;
static bool pendingSuppressKickOnce = false;

// ------------------------- Motion/BLE state -------------------------
static volatile bool debugMode = DEBUG_AT_STARTUP;


static int32_t appliedThrottle = 0;      
static int32_t targetThrottle  = 0;      
static Direction currentDirection = Direction::STOP;
static Direction targetDirection  = Direction::STOP;

// Ramp state 
static bool rampActive = false;
static uint32_t rampStartMs = 0;
static uint32_t rampDurationMs = 0;
static int32_t rampStartThrottle = 0;   
static int32_t rampTargetThrottle = 0;  
static Direction rampDirection = Direction::STOP;
static RampKind rampKind = RampKind::NONE;

// Train Name
static String cfgTrainName = "";   // persisted train name

// FLOOR/CEILING remap config
static int32_t cfgMinStart = 22;   // HW floor percent (0..100).
static int32_t cfgCeiling  = 90;   // HW ceiling percent (0..100). 

// Start assist config + state 
static int32_t cfgKickThrottle = 0;
static int32_t cfgKickMs = 0;
static int32_t cfgKickRampDownMs = 80;     // default for 2-param Kick
static int32_t cfgKickMaxApply   = 15;     // default for 2-param Kick

static bool kickActive = false;
static uint32_t kickEndMs = 0;
static int32_t kickHoldThrottle = 0;       
static Direction kickDirection = Direction::STOP;

// Post-kick continuation state (separate from reverse sequencing)
static bool postKickPending = false;
static Direction postKickDir = Direction::STOP;
static int32_t postKickFinalThr = 0;       
static bool postKickIsInstant = false;
static bool postKickIsMomentum = false;
static uint32_t postKickFullAccelMs = FULL_MOMENTUM_ACCEL_MS;
static uint32_t postKickFullDecelMs = FULL_MOMENTUM_DECEL_MS;

// Reverse sequencing state
static bool reversePending = false;
static PendingStage pendingStage = PendingStage::NONE;
static uint32_t pendingStageUntilMs = 0;
static int32_t pendingFinalTargetThrottle = 0; 
static Direction pendingFinalDirection = Direction::STOP;
static bool pendingFinalIsInstant = false;
static bool pendingFinalIsMomentum = false;

// BLE state
static bool bleConnected = false;
static bool handshakeOk = false;
static bool graceActive = false;
static uint32_t disconnectMs = 0;
static bool forcedStopLatched = false;
static uint32_t graceLastCountdownLogMs = 0;
static bool graceCountdownPrimed = false;

static NimBLECharacteristic* pTxChar = nullptr;
static NimBLEServer* pServerGlobal = nullptr;

static bool printPeriodic = DEBUG_PRINT_PERIODIC_ONLY_ON_MISMATCH;

// MTU-aware chunking state
static uint16_t g_peerMtu = 23;  

// ------------------------- Stop snap-to-zero after floor ramp (S/B/grace) -------------------------
static bool stopSnapAfterRamp = false;
static const char* stopSnapReason = "STOP-SNAP";

// ------------------------- Forward decls (keep local, minimize churn) -------------------------
static size_t getNotifyPayloadLimit(); 

// ------------------------- Advertising name helpers (TRAIN NAME -> ADV NAME) -------------------------
// - If cfgTrainName is empty -> advertise FW_NAME
// - If non-empty -> advertise cfgTrainName
static String getAdvertisedName() {
  if (cfgTrainName.length() > 0) return cfgTrainName;
  return String(FW_NAME);
}

static String clampAdvName(const String& s) {
  // Keep advertising name conservative to avoid truncation issues in ADV payload.
  // 29 is a practical upper bound for "Complete Local Name" in many common ADV layouts.
  const int32_t MAX_ADV_NAME = 29;
  if ((int32_t)s.length() <= MAX_ADV_NAME) return s;
  return s.substring(0, (unsigned int)MAX_ADV_NAME);
}

static void restartAdvertisingWithName() {
  NimBLEAdvertising* adv = NimBLEDevice::getAdvertising();
  if (!adv) return;

  String advName = clampAdvName(getAdvertisedName());

  // Update GAP device name (helps client caching behavior)
  NimBLEDevice::setDeviceName(advName.c_str());

  // If connected, advertising is typically stopped anyway; just updating GAP name is enough.
  if (bleConnected) {
    if (debugMode) {
      dbgPrintf("[BLE] GAP name updated (connected): %s", advName.c_str());
    }
    return;
  }

  adv->stop();

  // Rebuild advertising payload cleanly
  adv->reset();  // <-- use this instead of clearData()
  adv->addServiceUUID(SERVICE_UUID);
  adv->setName(advName.c_str());

  adv->start();

  if (debugMode) {
    dbgPrintf("[BLE] Advertising name='%s'", advName.c_str());
  }
}

// ------------------------- Status LED (GPIO2) -------------------------
// - Disconnected: blink continuously (double-blink search pattern)
// - Connected: solid ON
// - RX/TX while connected: briefly OFF (dip), then back ON
static const int LED_PIN = 2;

// Blink timing while disconnected
static const uint16_t LED_SEARCH_ON_1_MS   = 70;
static const uint16_t LED_SEARCH_GAP_MS    = 90;
static const uint16_t LED_SEARCH_ON_2_MS   = 70;
static const uint16_t LED_SEARCH_PAUSE_MS  = 700;

// Dip timing (brief OFF) on activity while connected
static const uint16_t LED_DIP_MS_RX = 150;
static const uint16_t LED_DIP_MS_TX = 100;

static bool     ledIsOn = false;
static uint32_t ledNextToggleMs = 0;

// Activity dip state
static bool     ledDipActive = false;
static uint32_t ledDipUntilMs = 0;

static inline void ledWrite(bool on) {
  digitalWrite(LED_PIN, on ? HIGH : LOW);
  ledIsOn = on;
}

static inline void ledInit() {
  pinMode(LED_PIN, OUTPUT);
  ledWrite(false);
  ledNextToggleMs = millis();
  ledDipActive = false;
  ledDipUntilMs = 0;
}

// Call on RX while connected: briefly force LED OFF
static inline void ledDipRx() {
  if (!bleConnected) return;         
  ledDipActive = true;
  ledDipUntilMs = millis() + (uint32_t)LED_DIP_MS_RX;
  ledWrite(false);
}

// Call on TX while connected: briefly force LED OFF
static inline void ledDipTx() {
  if (!bleConnected) return;
  ledDipActive = true;
  ledDipUntilMs = millis() + (uint32_t)LED_DIP_MS_TX;
  ledWrite(false);
}

// Non-blocking LED service; call often from loop()
static inline void ledService() {
  uint32_t now = millis();

  // 1) If connected: base state is SOLID ON, except during a dip
  if (bleConnected) {
    if (ledDipActive) {
      if ((int32_t)(now - ledDipUntilMs) >= 0) {
        ledDipActive = false;
        ledWrite(true); // back to solid ON
      }
    } else {
      if (!ledIsOn) ledWrite(true);
    }
    return;
  }

  // 2) If disconnected BUT grace is active: single-blink then double-blink pattern
  if (graceActive) {
    ledDipActive = false; // no dips in blink mode

    // Steps:
    // 0: single ON
    // 1: single OFF gap
    // 2: double ON #1
    // 3: double OFF gap
    // 4: double ON #2
    // 5: pause
    static uint8_t graceStep = 0;

    if ((int32_t)(now - ledNextToggleMs) < 0) return;

    switch (graceStep) {
      case 0: // single ON
        ledWrite(true);
        ledNextToggleMs = now + LED_SEARCH_ON_1_MS;
        graceStep = 1;
        break;

      case 1: // gap after single blink
        ledWrite(false);
        ledNextToggleMs = now + LED_SEARCH_PAUSE_MS;
        graceStep = 2;
        break;

      case 2: // double ON #1 (reuse search constants)
        ledWrite(true);
        ledNextToggleMs = now + LED_SEARCH_ON_1_MS;
        graceStep = 3;
        break;

      case 3: // double OFF gap (reuse search constants)
        ledWrite(false);
        ledNextToggleMs = now + LED_SEARCH_GAP_MS;
        graceStep = 4;
        break;

      case 4: // double ON #2 (reuse search constants)
        ledWrite(true);
        ledNextToggleMs = now + LED_SEARCH_ON_2_MS;
        graceStep = 5;
        break;

      default: // pause (reuse search constants), then repeat
        ledWrite(false);
        ledNextToggleMs = now + LED_SEARCH_PAUSE_MS;
        graceStep = 0;
        break;
    }
    return;
  }

  // 3) If disconnected: double-blink search pattern
  ledDipActive = false; // no dips in blink mode

  static uint8_t searchStep = 0;

  if ((int32_t)(now - ledNextToggleMs) < 0) return;

  switch (searchStep) {
    case 0:
      ledWrite(true);
      ledNextToggleMs = now + LED_SEARCH_ON_1_MS;
      searchStep = 1;
      break;

    case 1:
      ledWrite(false);
      ledNextToggleMs = now + LED_SEARCH_GAP_MS;
      searchStep = 2;
      break;

    case 2:
      ledWrite(true);
      ledNextToggleMs = now + LED_SEARCH_ON_2_MS;
      searchStep = 3;
      break;

    default: // pause
      ledWrite(false);
      ledNextToggleMs = now + LED_SEARCH_PAUSE_MS;
      searchStep = 0;
      break;
  }
}

// ------------------------- Helpers -------------------------
static inline void cancelPendingReverse() {
  reversePending = false;
  pendingStage = PendingStage::NONE;
  pendingSuppressKickOnce = false;
}

static inline int32_t clampI32(int32_t v, int32_t lo, int32_t hi) {
  if (v < lo) return lo;
  if (v > hi) return hi;
  return v;
}

static inline bool isDigitStr(const String& s) {
  if (s.length() == 0) return false;
  for (size_t i = 0; i < s.length(); i++) {
    if (!isDigit((unsigned char)s[i])) return false;
  }
  return true;
}

static bool isTrainNameAllowed(const String& s) {
  // Allowed: A-Z a-z 0-9 space
  for (size_t i = 0; i < s.length(); i++) {
    char c = s[i];
    if (c == ' ') continue;
    if (c >= '0' && c <= '9') continue;
    if (c >= 'A' && c <= 'Z') continue;
    if (c >= 'a' && c <= 'z') continue;
    return false;
  }
  return true;
}

static String sanitizeTrainNameToFit(const String& in) {
  // Enforce: no leading/trailing spaces + max length fits in ONE notify (incl \n)
  String s = in;
  s.trim(); // removes leading/trailing whitespace (including spaces)

  // Max reply length includes '\n' appended in bleNotifyLine()
  size_t maxLen = 0;
  size_t limit = getNotifyPayloadLimit();
  if (limit > 1) maxLen = limit - 1;

  if (maxLen > 0 && s.length() > maxLen) {
    s = s.substring(0, (unsigned int)maxLen);
    s.trim(); // re-trim in case truncation ends on space
  }
  return s;
}

static uint32_t scaledDurationMs(uint32_t fullScaleMs, int32_t deltaThrottleAbs) {
  deltaThrottleAbs = clampI32(deltaThrottleAbs, 0, 100);
  uint32_t dur = (uint32_t)((uint64_t)fullScaleMs * (uint64_t)deltaThrottleAbs / 100ULL);
  if (deltaThrottleAbs > 0 && dur == 0) dur = 1;
  return dur;
}

// ---- Obfuscation helper ----
static inline uint8_t rotl8(uint8_t x, uint8_t r) {
  r &= 7;
  return (uint8_t)((x << r) | (x >> ((8 - r) & 7)));
}
static inline uint8_t rotr8(uint8_t x, uint8_t r) {
  r &= 7;
  return (uint8_t)((x >> r) | (x << ((8 - r) & 7)));
}
static inline char nybToHex(uint8_t v) {
  v &= 0x0F;
  return (v < 10) ? (char)('0' + v) : (char)('A' + (v - 10));
}

static String obfuscate12(const String& in, uint32_t key) {
  if (in.length() != 12) return String("");  // enforce exactly 12 chars

  // Compress/mix 12 bytes -> 6 bytes by XOR pairing
  uint8_t b[6];
  for (int i = 0; i < 6; i++) {
    b[i] = (uint8_t)in[i] ^ (uint8_t)in[i + 6];
  }

  // Split key into bytes
  uint8_t k0 = (uint8_t)(key & 0xFF);
  uint8_t k1 = (uint8_t)((key >> 8) & 0xFF);
  uint8_t k2 = (uint8_t)((key >> 16) & 0xFF);
  uint8_t k3 = (uint8_t)((key >> 24) & 0xFF);

  // Per-byte mixing (repeatable)
  for (int i = 0; i < 6; i++) {
    uint8_t k = (uint8_t)(
      (uint8_t)(k0 + (uint8_t)(i * 17)) ^
      rotl8(k1, (uint8_t)i) ^
      rotr8(k2, (uint8_t)(6 - i)) ^
      (uint8_t)(k3 + (uint8_t)(i * 29))
    );

    uint8_t x = (uint8_t)(b[i] + (uint8_t)(i * 31));
    x ^= k;
    x = rotl8(x, (uint8_t)(1 + (i % 7)));
    x = (uint8_t)(x + (uint8_t)(k ^ (uint8_t)(i * 13)));
    b[i] = x;
  }

  // 6 bytes -> 12 hex chars
  char out[13];
  for (int i = 0; i < 6; i++) {
    out[i * 2 + 0] = nybToHex((uint8_t)(b[i] >> 4));
    out[i * 2 + 1] = nybToHex((uint8_t)(b[i] & 0x0F));
  }
  out[12] = '\0';
  return String(out);
}

// ------------------------- Persistence helpers -------------------------
static inline void markConfigDirty() {
  cfgDirty = true;
  cfgDirtySinceMs = millis();
}

static void loadConfigFromNvs() {
  Preferences prefs;
  if (!prefs.begin(NVS_NAMESPACE, /*readOnly*/ true)) {
    return;
  }

  uint32_t schema = prefs.getUInt("schema", 0);

  // Load with current in-RAM defaults as fallbacks (forward-compatible)
  cfgMinStart = clampI32(prefs.getInt("minStart", cfgMinStart), 0, 100);
  cfgCeiling  = clampI32(prefs.getInt("ceiling",  cfgCeiling),  0, 100);

  cfgKickThrottle   = clampI32(prefs.getInt("kThr",   cfgKickThrottle),   0, 100);
  cfgKickMs         = clampI32(prefs.getInt("kMs",    cfgKickMs),         0, 2000);
  cfgKickRampDownMs = clampI32(prefs.getInt("kRd",    cfgKickRampDownMs), 0, 2000);
  cfgKickMaxApply   = clampI32(prefs.getInt("kMax",   cfgKickMaxApply),   0, 100);

  cfgTrainName = prefs.getString("tName", cfgTrainName);
  cfgTrainName = sanitizeTrainNameToFit(cfgTrainName);

  prefs.end();

  // If schema missing or different, write back current values under our schema
  if (schema != NVS_SCHEMA_VERSION) {
    markConfigDirty();
  }
}

static void saveConfigToNvsNow() {
  Preferences prefs;
  if (!prefs.begin(NVS_NAMESPACE, /*readOnly*/ false)) {
    return;
  }

  prefs.putUInt("schema", NVS_SCHEMA_VERSION);

  prefs.putInt("minStart", (int)clampI32(cfgMinStart, 0, 100));
  prefs.putInt("ceiling",  (int)clampI32(cfgCeiling,  0, 100));

  prefs.putInt("kThr", (int)clampI32(cfgKickThrottle,   0, 100));
  prefs.putInt("kMs",  (int)clampI32(cfgKickMs,         0, 2000));
  prefs.putInt("kRd",  (int)clampI32(cfgKickRampDownMs, 0, 2000));
  prefs.putInt("kMax", (int)clampI32(cfgKickMaxApply,   0, 100));
  prefs.putString("tName", cfgTrainName);

  prefs.end();

  if (debugMode) {
    dbgPrintf("[CFG] Saved");
  }
}

static void processConfigSaveIfNeeded() {
  if (!cfgDirty) return;

  uint32_t now = millis();
  if ((uint32_t)(now - cfgDirtySinceMs) < CFG_SAVE_DEBOUNCE_MS) return;

  cfgDirty = false;
  saveConfigToNvsNow();
}

static void wipeConfigAndReboot() {
  // Safety: force STOP first
  // (This is a command-path operation; motor stop is the priority.)
  // stopMotorNow() will be called by the command handler prior to this.
  Preferences prefs;
  if (prefs.begin(NVS_NAMESPACE, /*readOnly*/ false)) {
    prefs.clear();
    prefs.end();
  }

  if (debugMode) {
    dbgPrintf("[CFG] Wiped, rebooting");
  }

  delay(150);
  ESP.restart();
}

// ------------------------- FLOOR/CEILING remap (MAPPED <-> HW) -------------------------
// MAPPED domain: 0..100, where 0 is STOP, 1..100 is commanded nonzero throttle.
// HW domain: 0..100, where 0 is STOP, otherwise [floor..ceiling] when either is set.
static inline void getFloorCeilingHw(int32_t& outFloor, int32_t& outCeil) {
  int32_t floorHw = clampI32(cfgMinStart, 0, 100);

  // cfgCeiling==0 means "no ceiling cap" (defaults to 100)
  int32_t ceilHw = (cfgCeiling <= 0) ? 100 : clampI32(cfgCeiling, 0, 100);

  // Ensure valid range
  if (ceilHw < floorHw) ceilHw = floorHw;

  outFloor = floorHw;
  outCeil  = ceilHw;
}

static int32_t mapMappedToHw(int32_t mappedThr) {
  mappedThr = clampI32(mappedThr, 0, 100);
  if (mappedThr <= 0) return 0;

  int32_t floorHw, ceilHw;
  getFloorCeilingHw(floorHw, ceilHw);

  // Identity fast path (no floor, no ceiling cap)
  if (floorHw <= 0 && ceilHw >= 100) return mappedThr;

  // Degenerate: any nonzero -> fixed HW
  if (floorHw >= 100) return 100;
  if (ceilHw <= 0) return 0;               // shouldn't happen after clamp logic
  if (ceilHw == floorHw) return floorHw;   // fixed output for any nonzero

  // Linear remap: mapped 1..100 -> HW floor..ceil
  // HW = floor + (mapped-1) * (ceil-floor) / 99  
  int32_t span = (ceilHw - floorHw);
  int32_t num  = (mappedThr - 1) * span;
  int32_t hw   = floorHw + (num + (99 / 2)) / 99; 
  return clampI32(hw, 0, 100);
}

static int32_t mapHwToMapped(int32_t hwThr) {
  hwThr = clampI32(hwThr, 0, 100);
  if (hwThr <= 0) return 0;

  int32_t floorHw, ceilHw;
  getFloorCeilingHw(floorHw, ceilHw);

  // Identity fast path
  if (floorHw <= 0 && ceilHw >= 100) return hwThr;

  // Degenerate: fixed range
  if (ceilHw == floorHw) return 1; // any nonzero HW corresponds to mapped=1 in this fixed-output mode

  // Clamp into [floor..ceil] for inverse mapping
  if (hwThr <= floorHw) return 1;
  if (hwThr >= ceilHw)  return 100;

  // Inverse of above (rounded):
  // mapped = 1 + (hw-floor) * 99 / (ceil-floor)
  int32_t denom  = (ceilHw - floorHw);
  int32_t num    = (hwThr - floorHw) * 99;
  int32_t mapped = 1 + (num + (denom / 2)) / denom; // rounded
  return clampI32(mapped, 0, 100);
}

static inline int32_t expectedHwFromStored() {
  if (appliedThrottle <= 0 || currentDirection == Direction::STOP) return 0;
  return mapMappedToHw(appliedThrottle);
}

static inline int32_t expectedHwFromMapped(int32_t mapped) {
  if (mapped <= 0) return 0;
  return mapMappedToHw(mapped);
}

// ------------------------- Timestamped Debug Printing (ONE println per line) -------------------------
static void dbgPrintf(const char* fmt, ...) {
  if (!debugMode) return;

  char line[768];
  int n = 0;

  uint32_t ms = millis();
  uint32_t totalSeconds = ms / 1000UL;
  uint32_t msec = ms % 1000UL;
  uint32_t sec  = totalSeconds % 60UL;
  uint32_t min  = (totalSeconds / 60UL) % 60UL;
  uint32_t hour = (totalSeconds / 3600UL) % 24UL;

  n = snprintf(line, sizeof(line),
               "%02lu:%02lu:%02lu.%03lu -> ",
               (unsigned long)hour,
               (unsigned long)min,
               (unsigned long)sec,
               (unsigned long)msec);

  if (n < 0 || (size_t)n >= sizeof(line)) return;

  va_list args;
  va_start(args, fmt);
  vsnprintf(line + n, sizeof(line) - (size_t)n, fmt, args);
  va_end(args);

  // Exactly ONE println to reduce interleaving across tasks
  Serial.println(line);
}

static void debugPrintln(const String& s) {
  if (!debugMode) return;
  dbgPrintf("%s", s.c_str());
}

// ------------------------- Throttle change logging -------------------------
static int32_t lastLoggedAppliedThrottle = -9999; 
static Direction lastLoggedDirection = Direction::STOP;
static const char* g_lastDbgReason = "BOOT";

static const char* dirStr(Direction d) {
  switch (d) {
    case Direction::STOP: return "STOP";
    case Direction::FWD:  return "FWD";
    case Direction::REV:  return "REV";
    default:              return "?";
  }
}

// ------------------------- Hardware readback (debug-only) -------------------------
static inline uint32_t readLedcDuty(uint8_t pwmChannel) {
  // Read back the PWM duty using the Arduino-ESP32 API (ledcRead).
  // This reflects the *configured LEDC duty register* (0..PWM_MAX_DUTY),
  // not motor physics (voltage/current/speed).
  // Note: If PWM isn’t initialized yet, return 0 to avoid junk reads.
  if (!pwmInitialized) return 0;
  return (uint32_t)ledcRead(pwmChannel);
}

static HwSnapshot getHwSnapshot() {
  HwSnapshot s{};

  // EN pins as seen by GPIO input buffers
  s.ren = (digitalRead(PIN_REN) == HIGH);
  s.len = (digitalRead(PIN_LEN) == HIGH);
  s.enabled = (s.ren && s.len);

  // PWM duties as currently configured in LEDC hardware
  s.dutyR = readLedcDuty(PWM_CH_R);
  s.dutyL = readLedcDuty(PWM_CH_L);

  // Derive hardware direction + throttle % (ACTUAL HW)
  if (!s.enabled || (s.dutyR == 0 && s.dutyL == 0)) {
    s.hwDir = Direction::STOP;
    s.hwThrottlePct = 0;
  } else if (s.dutyR > 0 && s.dutyL == 0) {
    s.hwDir = Direction::FWD;
    s.hwThrottlePct = (int32_t)(((uint64_t)s.dutyR * 100ULL + (PWM_MAX_DUTY / 2)) / (uint64_t)PWM_MAX_DUTY);
  } else if (s.dutyL > 0 && s.dutyR == 0) {
    s.hwDir = Direction::REV;
    s.hwThrottlePct = (int32_t)(((uint64_t)s.dutyL * 100ULL + (PWM_MAX_DUTY / 2)) / (uint64_t)PWM_MAX_DUTY);
  } else {
    // Should never happen in the logic (both sides driven), but useful as a fault indicator
    s.hwDir = Direction::STOP;
    s.hwThrottlePct = 0;
  }

  s.hwThrottlePct = clampI32(s.hwThrottlePct, 0, 100);
  return s;
}

static bool hwMatchesStored(const HwSnapshot& hw0) {
  // Treat stored STOP / appliedThrottle==0 as STOP expectation
  Direction storedDir = (appliedThrottle <= 0 || currentDirection == Direction::STOP)
                          ? Direction::STOP
                          : currentDirection;

  // Expected ACTUAL HW % based on stored MAPPED throttle + FLOOR/CEILING remap
  int32_t expectedHw = (storedDir == Direction::STOP) ? 0 : expectedHwFromStored();

  // During ramps/kicks, allow benign transient readback artifacts (LEDC latch timing etc.)
  const bool inTransition = (rampActive || kickActive);

  auto matchesOne = [&](const HwSnapshot& hw) -> bool {
    // Direction must match (but during transition, tolerate a brief STOP readback when expected > 0)
    if (hw.hwDir != storedDir) {
      if (inTransition && storedDir != Direction::STOP) {
        // Minimal Patch 1: allow the exact transient where EN is on but duty hasn't latched yet
        if (hw.enabled &&
            hw.hwDir == Direction::STOP &&
            hw.hwThrottlePct == 0 &&
            hw.dutyR == 0 && hw.dutyL == 0) {
          // accept
        } else {
          return false;
        }
      } else {
        return false;
      }
    }

    // Keep tolerance tight. Only slightly wider during transition.
    const int32_t tol = inTransition ? 3 : 2;
    int32_t diff = abs(hw.hwThrottlePct - expectedHw);

    // Also accept "enabled but 0 duty" transient during transition when expected > 0
    if (inTransition && expectedHw > 0 &&
        hw.enabled && hw.hwThrottlePct == 0 &&
        hw.dutyR == 0 && hw.dutyL == 0) {
      return true;
    }

    return (diff <= tol);
  };

  // First check
  if (matchesOne(hw0)) return true;

  // if we're in transition, re-sample once immediately to avoid one-tick readback artifacts
  if (inTransition) {
    HwSnapshot hw1 = getHwSnapshot();
    if (matchesOne(hw1)) return true;
  }

  return false;
}

static void logThrottleChangeIfNeeded(const char* reason) {
  if (!debugMode) return;

  // Periodic snapshot (C)
  static uint32_t lastPeriodicMs = 0;
  uint32_t now = millis();
  bool periodicDue = (lastPeriodicMs == 0) || ((now - lastPeriodicMs) >= DEBUG_HW_SNAPSHOT_PERIOD_MS);

  bool storedChanged =
      (appliedThrottle != lastLoggedAppliedThrottle) ||
      (currentDirection != lastLoggedDirection);

  // If neither stored change nor periodic due, do nothing.
  if (!storedChanged && !periodicDue) return;

  // Take periodic timestamp *now* so we don't hammer if we decide not to print.
  // (We still "sample" at the requested interval; printing is conditional.)
  if (periodicDue) lastPeriodicMs = now;

  // Read hardware snapshot (A)
  HwSnapshot hw = getHwSnapshot();
  bool match = hwMatchesStored(hw);

  // Decide whether to print:
  // - Always print on stored change
  // - For periodic: print only when mismatch (if toggle enabled), else print every period
  bool shouldPrint = false;

  if (storedChanged) {
    if (DEBUG_PRINT_STORED_ONLY_ON_MISMATCH) {
      shouldPrint = !match;
    } else {
      shouldPrint = true;
    }
  } else if (periodicDue) {
    if (printPeriodic) {
      shouldPrint = !match;
    } else {
      shouldPrint = true;
    }
  }

  if (!shouldPrint) {
    // Even if we suppress printing, we must advance the baseline
    // or "storedChanged" will remain true forever.
    if (storedChanged) {
      lastLoggedAppliedThrottle = appliedThrottle;
      lastLoggedDirection = currentDirection;
    }
    return;
  }

  Direction storedDir = (appliedThrottle <= 0 || currentDirection == Direction::STOP)
                          ? Direction::STOP
                          : currentDirection;

  int32_t storedMapped = clampI32(appliedThrottle, 0, 100);
  int32_t storedHwExpected = (storedDir == Direction::STOP) ? 0 : expectedHwFromStored();

  int32_t dDir = match ? 0 : (int32_t)((int)hw.hwDir - (int)storedDir);
  int32_t dHw  = match ? 0 : (int32_t)(hw.hwThrottlePct - storedHwExpected);

  dbgPrintf(
    "[THR] %s%s | STORED dir=%s M=%ld HW=%ld targetDir=%s targetM=%ld ramp=%d kick=%d pending=%d ble=%d latch=%d | "
    "HW EN=%d (REN=%d LEN=%d) dutyR=%lu dutyL=%lu hwDir=%s HW=%ld | %s dDir=%ld dHW=%ld",
    reason,
    (!storedChanged && periodicDue) ? " (periodic)" : "",
    dirStr(currentDirection),
    (long)storedMapped,
    (long)storedHwExpected,
    dirStr(targetDirection),
    (long)targetThrottle,
    rampActive ? 1 : 0,
    kickActive ? 1 : 0,
    reversePending ? 1 : 0,
    bleConnected ? 1 : 0,
    forcedStopLatched ? 1 : 0,
    hw.enabled ? 1 : 0,
    hw.ren ? 1 : 0,
    hw.len ? 1 : 0,
    (unsigned long)hw.dutyR,
    (unsigned long)hw.dutyL,
    dirStr(hw.hwDir),
    (long)hw.hwThrottlePct,
    match ? "OK" : "MISMATCH",
    (long)dDir,
    (long)dHw
  );

  // Update baseline only when stored state changed
  if (storedChanged) {
    lastLoggedAppliedThrottle = appliedThrottle;
    lastLoggedDirection = currentDirection;
  }
}

static inline void setApplied(Direction dir, int32_t thrMapped, const char* reason) {
  thrMapped = clampI32(thrMapped, 0, 100);
  currentDirection = (thrMapped == 0) ? Direction::STOP : dir;
  appliedThrottle  = thrMapped;

  // Debug-only: remember why the last state changed.
  g_lastDbgReason = reason;

  // DO NOT log here anymore 
}

static inline void setTarget(Direction dir, int32_t thrMapped, const String& reason) {
  thrMapped = clampI32(thrMapped, 0, 100);
  targetDirection = (thrMapped == 0) ? Direction::STOP : dir;
  targetThrottle  = thrMapped;

  if (debugMode) {
    dbgPrintf("[TGT] %s | targetDir=%s targetM=%ld", reason.c_str(), dirStr(targetDirection), (long)targetThrottle);
  }
}

static uint32_t throttleToDutyHw(int32_t thrHw) {
  thrHw = clampI32(thrHw, 0, 100);
  return (uint32_t)((uint64_t)thrHw * (uint64_t)PWM_MAX_DUTY / 100ULL);
}

static inline void driverEnable(bool en) {
  digitalWrite(PIN_REN, en ? HIGH : LOW);
  digitalWrite(PIN_LEN, en ? HIGH : LOW);
}

static void applyPwmOutputs(Direction dir, int32_t thrMapped) {
  // Convert mapped -> HW (FLOOR/CEILING remap), then output HW duty.
  int32_t thrHw = (dir == Direction::STOP || thrMapped <= 0) ? 0 : mapMappedToHw(thrMapped);

  if (thrHw <= 0 || dir == Direction::STOP) {
    ledcWrite(PWM_CH_R, 0);
    ledcWrite(PWM_CH_L, 0);
    driverEnable(false);   // EN LOW = true off/coast
    return;
  }

  uint32_t duty = throttleToDutyHw(thrHw);

  if (dir == Direction::FWD) {
    driverEnable(true);
    ledcWrite(PWM_CH_R, duty);
    ledcWrite(PWM_CH_L, 0);
  } else { // REV
    driverEnable(true);
    ledcWrite(PWM_CH_R, 0);
    ledcWrite(PWM_CH_L, duty);
  }
}

static void stopMotorNow(const char* reason) {
  // Clear stop-snap staging
  stopSnapAfterRamp = false;
  stopSnapReason = "STOP-SNAP";

  setApplied(Direction::STOP, 0, reason);
  setTarget(Direction::STOP, 0, "stopMotorNow target");

  // Stop any ramp
  rampActive = false;
  rampKind = RampKind::NONE;
  rampStartMs = 0;
  rampDurationMs = 0;
  rampStartThrottle = 0;
  rampTargetThrottle = 0;
  rampDirection = Direction::STOP;

  // Stop any kick + clear kick bookkeeping
  kickActive = false;
  kickEndMs = 0;
  kickHoldThrottle = 0;
  kickDirection = Direction::STOP;

  // Clear post-kick continuation
  postKickPending = false;
  postKickDir = Direction::STOP;
  postKickFinalThr = 0;
  postKickIsInstant = false;
  postKickIsMomentum = false;
  postKickFullAccelMs = FULL_MOMENTUM_ACCEL_MS;
  postKickFullDecelMs = FULL_MOMENTUM_DECEL_MS;

  // Clear any reverse sequencing
  reversePending = false;
  pendingStage = PendingStage::NONE;
  pendingStageUntilMs = 0;
  pendingFinalTargetThrottle = 0;
  pendingFinalDirection = Direction::STOP;
  pendingFinalIsInstant = false;
  pendingFinalIsMomentum = false;
  pendingSkipDirDelay = false;
  pendingSuppressKickOnce = false;

  applyPwmOutputs(Direction::STOP, 0);
  driverEnable(false);  // TRUE STOP: coast/off

  if (debugMode) {
    dbgPrintf("[STOP] %s", reason);
  }
}

// ---- Dual-value state strings (mapped + HW) ----
static String getStateString() {
  // STORED/APPLIED: show both MAPPED + EXPECTED HW (after remap)
  int32_t m = clampI32(appliedThrottle, 0, 100);
  int32_t hw = (currentDirection == Direction::STOP || m <= 0) ? 0 : mapMappedToHw(m);

  if (m <= 0 || currentDirection == Direction::STOP) {
    return "STOPPED M0 HW0";
  }
  if (currentDirection == Direction::FWD) {
    return "FWD M" + String(m) + " HW" + String(hw);
  }
  if (currentDirection == Direction::REV) {
    return "REV M" + String(m) + " HW" + String(hw);
  }
  return "STOPPED M0 HW0";
}

static String getHwStateString() {
  // HARDWARE: show both mapped-equivalent + measured HW
  HwSnapshot hw = getHwSnapshot();
  int32_t hwPct = clampI32(hw.hwThrottlePct, 0, 100);
  int32_t mEq = (hw.hwDir == Direction::STOP || hwPct <= 0) ? 0 : mapHwToMapped(hwPct);

  if (hwPct <= 0 || hw.hwDir == Direction::STOP) {
    return "HW-STOPPED M0 HW0";
  }
  if (hw.hwDir == Direction::FWD) {
    return "HW-FWD M" + String(mEq) + " HW" + String(hwPct);
  }
  if (hw.hwDir == Direction::REV) {
    return "HW-REV M" + String(mEq) + " HW" + String(hwPct);
  }
  return "HW-STOPPED M0 HW0";
}

// ------------------------- MTU-aware BLE notify (chunked) -------------------------
static size_t getNotifyPayloadLimit() {
  const size_t fallback = 20;
  if (!bleConnected) return fallback;

  uint16_t peerMtu = g_peerMtu;
  if (peerMtu < 23) return fallback;

  size_t payload = (size_t)peerMtu - 3;
  if (payload < 20) payload = 20;
  return payload;
}

static void bleNotifyChunked(const String& text) {
  if (!pTxChar || !bleConnected) return;

  const size_t limit = getNotifyPayloadLimit();
  const size_t n = text.length();

  size_t i = 0;
  while (i < n) {
    size_t take = n - i;
    if (take > limit) take = limit;

    String part = text.substring((unsigned int)i, (unsigned int)(i + take));
    pTxChar->setValue(part.c_str());

    // TX briefly turns LED OFF (dip) while connected/solid
    ledDipTx();

    pTxChar->notify();
    i += take;
  }
}

// ------------------------- BLE line endings (clean terminal output) -------------------------
static const char* BLE_EOL = "\n";

static void logReplyForSerial(const String& text) {
  if (!debugMode) return;

  // Make a cleaned copy for Serial (strip CR/LF)
  String clean = text;
  clean.replace("\r", "");
  clean.replace("\n", "");

  dbgPrintf("REPLY:%s", clean.c_str());
}

static void bleNotifyLine(const String& text) {
  // Log once per logical reply (before chunking)
  logReplyForSerial(text);

  bleNotifyChunked(text + String(BLE_EOL));
}

static void sendACK(const String& original)  { bleNotifyLine("ACK:" + original); }
static void sendERR(const String& original)  { bleNotifyLine("ERR:" + original); }

// ------------------------- Ramp engine (fixed-point smoothstep) -------------------------
static int32_t smoothstepEasedThrottle(int32_t startThr, int32_t targetThr, uint32_t elapsedMs, uint32_t durationMs) {
  if (durationMs == 0) return targetThr;

  int32_t p = (int32_t)((uint64_t)elapsedMs * (uint64_t)P_SCALE / (uint64_t)durationMs);
  p = clampI32(p, 0, P_SCALE);

  int64_t p64 = (int64_t)p;
  int64_t p2  = p64 * p64;
  int64_t p3  = p2  * p64;

  int64_t term1 = (3LL * p2) / (int64_t)P_SCALE;
  int64_t term2 = (2LL * p3) / ((int64_t)P_SCALE * (int64_t)P_SCALE);
  int32_t e = (int32_t)(term1 - term2); // 0..P_SCALE

  int32_t delta = (targetThr - startThr);
  int32_t out = startThr + (int32_t)(((int64_t)delta * (int64_t)e) / (int64_t)P_SCALE);
  return clampI32(out, 0, 100);
}

static inline void primeRampImmediateTick(uint32_t primeElapsedMs = 20) {
  if (!rampActive) return;

  // Force a tiny elapsed so integer fixed-point smoothstep produces a non-zero change.
  // 20ms is usually enough to overcome rounding but still feels "instant".
  int32_t newThr = smoothstepEasedThrottle(
      rampStartThrottle,
      rampTargetThrottle,
      primeElapsedMs,
      rampDurationMs);

  // If it still didn't move (rare with small ramps), bump once more.
  if (newThr == rampStartThrottle && rampDurationMs > 0) {
    newThr = smoothstepEasedThrottle(
        rampStartThrottle,
        rampTargetThrottle,
        primeElapsedMs * 2,
        rampDurationMs);
  }

  setApplied(rampDirection, newThr, "Ramp prime");
  applyPwmOutputs(currentDirection, appliedThrottle);
}

static void cancelAllMotionActivities() {
  // Cancels any active ramp/kick AND cancels any kick-related scheduled continuation.
  rampActive = false;
  kickActive = false;

  // If we cancel motion, also cancel any stored post-kick continuation
  postKickPending = false;

  // Do NOT clear reversePending/pendingStage here (used for stop-first reversing).
}

static void startRamp(Direction dirDuringRamp, int32_t startThrMapped, int32_t endThrMapped, uint32_t durationMs, RampKind kind) {
  // NOTE: startRamp() calls cancelAllMotionActivities().
  cancelAllMotionActivities();

  startThrMapped = clampI32(startThrMapped, 0, 100);
  endThrMapped   = clampI32(endThrMapped, 0, 100);

  rampKind = kind;
  rampDirection = dirDuringRamp;

  targetDirection = (endThrMapped == 0) ? Direction::STOP : dirDuringRamp;
  targetThrottle  = endThrMapped;

  if (durationMs == 0 || startThrMapped == endThrMapped) {
    setApplied(dirDuringRamp, endThrMapped, "Ramp immediate");
    applyPwmOutputs(currentDirection, appliedThrottle);
    return;
  }

  rampActive = true;
  rampStartMs = millis();
  rampDurationMs = durationMs;
  rampStartThrottle = startThrMapped;
  rampTargetThrottle = endThrMapped;

  currentDirection = (startThrMapped == 0 && endThrMapped == 0) ? Direction::STOP : dirDuringRamp;

  // NEW: make ramp react immediately (avoid smoothstep integer "dead" start)
  primeRampImmediateTick(20);
}

// ------------------------- Start assist (KICK) -------------------------
static int32_t effectiveStartTargetThrottle(int32_t requestedThr, bool /*startingFromStop*/) {
  // With FLOOR/CEILING remap, "effective target" no longer needs to clamp/boost.
  // Keep the function to minimize churn; KICK logic still uses startingFromStop.
  return clampI32(requestedThr, 0, 100);
}

static bool shouldKickOnStart(bool startingFromStop, int32_t finalTargetThrMapped) {
  if (!startingFromStop) return false;
  if (finalTargetThrMapped <= 0) return false;
  if (cfgKickThrottle <= 0 || cfgKickMs <= 0) return false;

  // only kick for low commanded (MAPPED) values
  if (finalTargetThrMapped > cfgKickMaxApply) return false;

  return true;
}

static void beginKick(Direction dir,
                      int32_t finalTargetThrMapped,
                      bool afterKickIsInstant,
                      bool afterKickIsMomentum,
                      uint32_t accelMs,
                      uint32_t decelMs)
{
  int32_t kickThrMapped = clampI32(cfgKickThrottle, 0, 100);

  kickActive = true;
  kickDirection = dir;
  kickHoldThrottle = kickThrMapped;
  kickEndMs = millis() + (uint32_t)cfgKickMs;

  setApplied(dir, kickHoldThrottle, "KICK begin");
  applyPwmOutputs(currentDirection, appliedThrottle);

  // Store what to do after the kick finishes (DON'T reuse reversePending)
  postKickPending = true;
  postKickDir = dir;
  postKickFinalThr = finalTargetThrMapped;
  postKickIsInstant = afterKickIsInstant;
  postKickIsMomentum = afterKickIsMomentum;

  // Preserve which ramp constants were active when the command was issued
  postKickFullAccelMs = accelMs;
  postKickFullDecelMs = decelMs;

  setTarget(dir, finalTargetThrMapped, "KICK target");
}

static void continueAfterKickIfNeeded() {
  if (!postKickPending) return;

  Direction dir = postKickDir;
  int32_t finalThr = clampI32(postKickFinalThr, 0, 100);

  // Done with "kick phase"
  postKickPending = false;

  // Recovery target:
  // - Instant command: recover toward finalThr quickly
  // - Ramped command: recover to 0 quickly, then let ramp engine handle inertia up to finalThr
  const bool isInstant = postKickIsInstant;
  int32_t recoverTo = isInstant ? finalThr : 0;

  uint32_t rd = (uint32_t)cfgKickRampDownMs;

  // ---- Stage continuation FIRST (for ramped commands only) ----
  // IMPORTANT: do NOT set WAIT_DIR_DELAY yet. Let processRamp() arm it when we actually hit 0.
  if (!isInstant) {
    pendingFinalDirection = dir;
    pendingFinalTargetThrottle = finalThr;
    pendingFinalIsInstant = false;

    // Continuation behavior deterministic (momentum-like), while preserving accel/decel constants.
    pendingFinalIsMomentum = true;

    pendingFullAccelMs = postKickFullAccelMs;
    pendingFullDecelMs = postKickFullDecelMs;

    pendingSkipDirDelay = true;     // no direction-delay for post-kick continuation
    reversePending = true;
    pendingStage = PendingStage::NONE;
    pendingSuppressKickOnce = true;
  }

  // ---- Now perform the fast recovery ----
  if (rd == 0) {
    // Immediate recovery
    setApplied(dir, recoverTo, "KICK recover immediate");
    applyPwmOutputs(currentDirection, appliedThrottle);

    // If ramped command: we must manually arm the continuation because no ramp completion will occur
    if (!isInstant && recoverTo == 0 && reversePending) {
      pendingStage = PendingStage::WAIT_DIR_DELAY;
      pendingStageUntilMs = millis();  // no delay
      pendingSkipDirDelay = false;     // consume it here
    }

    return;
  }

  // Recovery ramp (nonzero duration). When it completes at 0, processRamp() will arm WAIT_DIR_DELAY.
  startRamp(dir, appliedThrottle, recoverTo, rd, RampKind::QUICKSTOP);

  // If it was an instant command, we're done after the recovery ramp completes.
}

// ------------------------- Motion command execution -------------------------
static void executeStopRamp(RampKind kind, bool honorMinstartFloorSnap) {
  cancelAllMotionActivities(); // preserve reverse sequencing

  int32_t startThr = clampI32(appliedThrottle, 0, 100);

  // If we're already stopped, just ensure true stop.
  if (startThr <= 0 || currentDirection == Direction::STOP) {
    stopMotorNow("Stop ramp while stopped");
    return;
  }

  uint32_t full = FULL_QUICKSTOP_MS;
  if (kind == RampKind::BRAKE) full = FULL_BRAKE_MS;
  if (kind == RampKind::MOMENTUM) full = FULL_MOMENTUM_DECEL_MS;

  Direction dir = currentDirection;

  if (honorMinstartFloorSnap && cfgMinStart > 0) {
    // Ramp down to mapped=1 (HW floor), then snap to true stop.
    int32_t endMapped = 1;
    int32_t deltaAbs = abs(startThr - endMapped);
    uint32_t dur = scaledDurationMs(full, deltaAbs);

    setTarget(Direction::STOP, 0, "Stop ramp target (floor+snap)");

    stopSnapAfterRamp = true;
    stopSnapReason = (kind == RampKind::BRAKE) ? "BRAKE snap" :
                     (kind == RampKind::MOMENTUM) ? "MOMENTUM snap" :
                     "QUICKSTOP snap";

    if (startThr <= 1) {
      // already at floor; snap immediately
      stopMotorNow(stopSnapReason);
      return;
    }

    startRamp(dir, startThr, endMapped, dur, kind);
    return;
  }

  // Default / stop-first reverse behavior: ramp to true 0.
  int32_t deltaAbs = abs(startThr);
  uint32_t dur = scaledDurationMs(full, deltaAbs);

  setTarget(Direction::STOP, 0, "Stop ramp target");

  startRamp(dir, startThr, 0, dur, kind);
}

static void scheduleReverseAfterStop(Direction finalDir, int32_t finalThrMapped, bool isInstant, bool isMomentum) {
  pendingFinalTargetThrottle = finalThrMapped;
  pendingFinalDirection = finalDir;
  pendingFinalIsInstant = isInstant;
  pendingFinalIsMomentum = isMomentum;

  reversePending = true;
  pendingStage = PendingStage::NONE;
}

static void executeInstant(Direction dir, int32_t requestedThrMapped) {
  stopSnapAfterRamp = false;
  stopSnapReason = "STOP-SNAP";
  // New motion command overrides any previously queued reverse
  cancelPendingReverse();
  cancelAllMotionActivities();

  int32_t thr = clampI32(requestedThrMapped, 0, 100);

  setTarget(dir, thr, "Instant command target");

  if (thr == 0) {
    stopMotorNow("Instant throttle=0");
    return;
  }

  // Direction change while moving -> stop first (true stop for reversing safety)
  if (currentDirection != Direction::STOP && currentDirection != dir) {
    scheduleReverseAfterStop(dir, thr, /*instant*/true, /*momentum*/false);
    executeStopRamp(RampKind::QUICKSTOP, /*honorMinstartFloorSnap*/false);
    return;
  }

  bool startingFromStop = (appliedThrottle == 0 && currentDirection == Direction::STOP);
  int32_t effThr = effectiveStartTargetThrottle(thr, startingFromStop);

  setTarget(dir, effThr, "Instant effective target");

  if (shouldKickOnStart(startingFromStop, effThr)) {
    beginKick(dir, effThr,
      /*afterKickIsInstant*/true,
      /*afterKickIsMomentum*/false,
      /*accelMs*/0,
      /*decelMs*/0);
    return;
  }

  setApplied(dir, effThr, "Instant apply");
  applyPwmOutputs(currentDirection, appliedThrottle);
}

static void executeRampedMove(Direction dir,
                              int32_t requestedThrMapped,
                              uint32_t fullScaleAccelMs,
                              uint32_t fullScaleDecelMs,
                              RampKind stopFirstRampKind,
                              const char* tagReason) {
  stopSnapAfterRamp = false;
  stopSnapReason = "STOP-SNAP";
  // New motion command overrides any previously queued reverse
  cancelPendingReverse();
  cancelAllMotionActivities();

  int32_t thr = clampI32(requestedThrMapped, 0, 100);
  setTarget(dir, thr, String(tagReason) + " target");

  // If target is 0, user is stopping: honor floor+snap behavior.
  if (thr == 0) {
    executeStopRamp(stopFirstRampKind, /*honorMinstartFloorSnap*/true);
    return;
  }

  // Direction change while moving -> stop first, then wait, then ramp up (true stop)
  if (currentDirection != Direction::STOP && currentDirection != dir) {
    // Store what to do after stop + delay:
    scheduleReverseAfterStop(dir, thr, /*isInstant*/false, /*isMomentum*/true);

    // Store WHICH ramp constants to use after the delay
    pendingFullAccelMs = fullScaleAccelMs;
    pendingFullDecelMs = fullScaleDecelMs;

    // Ramp to zero first (true stop for reversing safety)
    executeStopRamp(stopFirstRampKind, /*honorMinstartFloorSnap*/false);
    return;
  }

  bool startingFromStop = (appliedThrottle == 0 && currentDirection == Direction::STOP);
  int32_t effTarget = effectiveStartTargetThrottle(thr, startingFromStop);

  setTarget(dir, effTarget, String(tagReason) + " effective target");

  if (startingFromStop) {
    currentDirection = dir; // bookkeeping
  }

  if (shouldKickOnStart(startingFromStop, effTarget)) {
    // After kick: ramped (not instant). Preserve quick vs momentum ramp constants.
    pendingFullAccelMs = fullScaleAccelMs;
    pendingFullDecelMs = fullScaleDecelMs;

    beginKick(dir, effTarget,
          /*afterKickIsInstant*/false,
          /*afterKickIsMomentum*/true,
          fullScaleAccelMs,
          fullScaleDecelMs);
    return;
  }

  int32_t startThr = clampI32(appliedThrottle, 0, 100);
  int32_t deltaAbs = abs(effTarget - startThr);
  if (deltaAbs == 0) {
    applyPwmOutputs(currentDirection, appliedThrottle);
    return;
  }

  uint32_t full = (effTarget > startThr) ? fullScaleAccelMs : fullScaleDecelMs;
  uint32_t dur  = scaledDurationMs(full, deltaAbs);

  startRamp(dir, startThr, effTarget, dur, RampKind::MOMENTUM);
}

static void executeMomentum(Direction dir, int32_t requestedThrMapped) {
  executeRampedMove(dir,
                    requestedThrMapped,
                    FULL_MOMENTUM_ACCEL_MS,
                    FULL_MOMENTUM_DECEL_MS,
                    RampKind::MOMENTUM,   // stop-first uses momentum decel feel
                    "Momentum");
}

static void executeQuickRamp(Direction dir, int32_t requestedThrMapped) {
  executeRampedMove(dir,
                    requestedThrMapped,
                    FULL_QUICKRAMP_ACCEL_MS,
                    FULL_QUICKRAMP_DECEL_MS,
                    RampKind::QUICKSTOP,  // stop-first: quick down feels snappier for Q
                    "QuickRamp");
}

// ------------------------- BLE callbacks -------------------------
class ServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo) override {
    (void)pServer;
    bleConnected = true;
    handshakeOk = false;

    g_peerMtu = connInfo.getMTU();
    if (g_peerMtu < 23) g_peerMtu = 23;

    // When connected, LEDService will hold SOLID ON automatically
    if (graceActive) {
      debugPrintln("[BLE] Reconnected, grace cancelled");
    } else {
      debugPrintln("[BLE] Connected");
    }
    graceActive = false;
    disconnectMs = 0;          // prevent stale timestamp from ever expiring grace
    graceCountdownPrimed = false;
    graceLastCountdownLogMs = 0;

    if (debugMode) {
      dbgPrintf("[BLE] MTU=%u", (unsigned)g_peerMtu);
    }
  }

  void onDisconnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo, int reason) override {
    (void)pServer; (void)connInfo; (void)reason;

    bleConnected = false;
    handshakeOk = false;
    g_peerMtu = 23;

    // When disconnected, LEDService will blink automatically
    graceActive = true;
    disconnectMs = millis();

    graceCountdownPrimed = false;
    graceLastCountdownLogMs = 0;

    if (debugMode) {
      dbgPrintf("[BLE] Disconnected, grace started (%lums)", (unsigned long)BLE_GRACE_MS);
    }

    // Ensure ADV name matches current cfgTrainName (or FW_NAME if empty)
    restartAdvertisingWithName();
  }
};

class RxCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* pCharacteristic, NimBLEConnInfo& connInfo) override {
    (void)connInfo;

    std::string val = pCharacteristic->getValue();
    if (val.empty()) return;

    // RX briefly turns LED OFF (dip) while connected/solid
    ledDipRx();

    String preserved = String(val.c_str());
    String original = preserved;

    original.trim();
    original.replace("\r", "");
    original.replace("\n", "");

    String upper = original;
    upper.toUpperCase();

    if (debugMode) {
      dbgPrintf("[CMD] %s", preserved.c_str());
    }

    bool allowMotionNow = !(forcedStopLatched && !bleConnected);

    // STATE (no ACK, raw response only)
    if (upper == "?") {
      bleNotifyLine(getHwStateString());
      return;
    }
    if (upper == "??") {
      bleNotifyLine(getStateString());
      return;
    }

    // VERSION
    if (upper == "V") {
      bleNotifyLine(String(FW_VERSION));
      return;
    }

    // HANDSHAKE STATUS QUERY
    // I? -> if not handshaken: ERR:ConnFailed
    //       if handshaken:     ACK:Connected
    if (upper == "I?") {
      if (!handshakeOk) sendERR("ConnFailed");
      else sendACK("Connected");
      return;
    }

    // CHIP ID HANDSHAKE (client sends I,<obf12>) 
    // - If matches expected obfuscated gEfuseHex => RAW reply: I:CONNECTED
    // - Else => ERR:ConnFailed (via sendERR wrapper)
    if (upper.startsWith("I,")) {
      // Extract payload after "I," from the already-trimmed/cleaned original
      String payload = original.substring(2);
      payload.trim();

      // Expected is the same obfuscation used for debug printing
      String expected = obfuscate12(gEfuseHex, (uint32_t)0xC0FFEE12UL);

      // Case-insensitive compare
      String payloadU  = payload;  payloadU.toUpperCase();
      String expectedU = expected; expectedU.toUpperCase();

      if (payloadU == expectedU && expectedU.length() > 0) {
        handshakeOk = true;
        bleNotifyLine("I:CONNECTED");
      } else {
        handshakeOk = false;
        sendERR("ConnFailed"); // -> "ERR:ConnFailed"
      }
      return;
    }
    
    // CHIP ID (cached eFuse MAC) - RAW reply
    if (upper == "I") {
      // IMPORTANT: do NOT change BLE reply behavior.
      // Debug-only: also print obfuscated 12-char form to Serial Monitor.
      if (debugMode) {
        String obf = obfuscate12(gEfuseHex, (uint32_t)0xC0FFEE12UL);
        dbgPrintf("[ID] obf12=%s", obf.c_str());
      }

      bleNotifyLine("I:" + gEfuseHex);
      return;
    }

    // WIPE CONFIG + REBOOT
    if (upper == "X") {
      stopMotorNow("Config wipe+reboot");
      sendACK(preserved);
      delay(120);
      wipeConfigAndReboot();
      return;
    }

    // TRAIN NAME (persisted)
    // T            : query -> replies with current name (raw)
    // T,<name>     : set   -> validates, trims, persists, replies with stored name (raw)
    if (upper == "T") {
      // If stored name is empty, return the advertised default (FW_NAME).
      bleNotifyLine(getAdvertisedName());
      return;
    }

    if (upper.startsWith("T,")) {
      // Preserve original case for the actual stored name
      String namePart = original.substring(2); // everything after "T,"
      String cleaned = sanitizeTrainNameToFit(namePart);

      if (cleaned.length() == 0) { sendERR(preserved); return; }
      if (!isTrainNameAllowed(cleaned)) { sendERR(preserved); return; }

      if (cleaned != cfgTrainName) {
        cfgTrainName = cleaned;
        markConfigDirty();

        // Update advertised name immediately (cfgTrainName -> ADV name, empty -> FW_NAME)
        restartAdvertisingWithName();
      }

      // Confirmation: reply with the name only (raw)
      bleNotifyLine(cfgTrainName);
      return;
    }

    // DEBUG
    if (upper == "D1") {
      debugMode = true;
      Serial.begin(115200);

      // Reset logging baseline so first change prints
      lastLoggedAppliedThrottle = -9999;
      lastLoggedDirection = (Direction)255;

      dbgPrintf("[DEBUG] ON");
      dbgPrintf("[FW] %s v%s", FW_NAME, FW_VERSION);
      dbgPrintf("[BOOT] ready");

      if (bleConnected) {
        dbgPrintf("[BLE] MTU=%u", (unsigned)g_peerMtu);
      }

      // Force initial state print
      logThrottleChangeIfNeeded("Debug enabled (baseline)");

      sendACK(preserved);
      return;
    }
    if (upper == "D0") {
      sendACK(preserved);
      debugMode = false;
      return;
    }

    // PERIODIC PRINT CONTROL
    // P0 = periodic prints ONLY when mismatch (default behavior)
    // P1 = periodic prints every period (even if OK)
    if (upper == "P0") {
      printPeriodic = true;

      if (debugMode) {
        dbgPrintf("[CFG] Periodic prints: ONLY ON MISMATCH");
      }

      sendACK(preserved);
      return;
    }

    if (upper == "P1") {
      printPeriodic = false;

      if (debugMode) {
        dbgPrintf("[CFG] Periodic prints: ALWAYS (every period)");
      }

      sendACK(preserved);
      return;
    }

    // FLOOR (MINSTART): M<n> / M query (RAW)
    if (upper == "M") {
      bleNotifyLine("M" + String(clampI32(cfgMinStart, 0, 100)));
      return;
    }

    // CEILING: C<n> / C query (RAW, effective ceiling)
    if (upper == "C") {
      int32_t floorHw = 0, ceilHw = 0;
      getFloorCeilingHw(floorHw, ceilHw);
      (void)floorHw;
      bleNotifyLine("C" + String(clampI32(ceilHw, 0, 100)));
      return;
    }

    // KICK query (RAW): K<t>,<ms>,<rampDownMs>,<maxApply>
    if (upper == "K") {
      bleNotifyLine("K" +
        String(clampI32(cfgKickThrottle, 0, 100)) + "," +
        String(clampI32(cfgKickMs, 0, 2000)) + "," +
        String(clampI32(cfgKickRampDownMs, 0, 2000)) + "," +
        String(clampI32(cfgKickMaxApply, 0, 100)));
      return;
    }

    // FLOOR (MINSTART): M<n>
    if (upper.startsWith("M")) {
      String nStr = original.substring(String("M").length());
      nStr.trim();
      if (!isDigitStr(nStr)) { sendERR(preserved); return; }

      int32_t old = cfgMinStart;
      cfgMinStart = clampI32(nStr.toInt(), 0, 100);

      if (debugMode) {
        dbgPrintf("[CFG] M=%ld (HW floor remap)", (long)cfgMinStart);
      }

      if (cfgMinStart != old) markConfigDirty();

      // If currently moving, do NOT force-change mapped throttle; output will update automatically
      // because applyPwmOutputs uses mapping every loop tick / ramp tick.

      sendACK(preserved);
      return;
    }

    // CEILING: C<n>
    if (upper.startsWith("C")) {
      String nStr = original.substring(String("C").length());
      nStr.trim();
      if (!isDigitStr(nStr)) { sendERR(preserved); return; }

      int32_t old = cfgCeiling;
      cfgCeiling = clampI32(nStr.toInt(), 0, 100);

      if (debugMode) {
        if (cfgCeiling <= 0) {
          dbgPrintf("[CFG] C=0 (HW ceiling disabled; defaults to 100)");
        } else {
          dbgPrintf("[CFG] C=%ld (HW ceiling cap)", (long)cfgCeiling);
        }
      }

      if (cfgCeiling != old) markConfigDirty();

      sendACK(preserved);
      return;
    }

    // KICK<t>,<ms>[,<rampDownMs>,<maxApply>]
    if (upper.startsWith("K")) {
      String args = original.substring(1);
      args.trim();

      // Tokenize by comma (max 4 tokens)
      String tok[4];
      int tokCount = 0;

      while (tokCount < 4) {
        int comma = args.indexOf(',');
        if (comma < 0) { tok[tokCount++] = args; break; }
        tok[tokCount++] = args.substring(0, comma);
        args = args.substring(comma + 1);
        args.trim();
      }

      if (tokCount != 2 && tokCount != 4) { sendERR(preserved); return; }

      for (int i = 0; i < tokCount; i++) tok[i].trim();

      if (!isDigitStr(tok[0]) || !isDigitStr(tok[1])) { sendERR(preserved); return; }

      int32_t oldThr = cfgKickThrottle;
      int32_t oldMs  = cfgKickMs;
      int32_t oldRd  = cfgKickRampDownMs;
      int32_t oldMax = cfgKickMaxApply;

      cfgKickThrottle = clampI32(tok[0].toInt(), 0, 100);
      cfgKickMs       = clampI32(tok[1].toInt(), 0, 2000);

      if (tokCount == 4) {
        if (!isDigitStr(tok[2]) || !isDigitStr(tok[3])) { sendERR(preserved); return; }
        cfgKickRampDownMs = clampI32(tok[2].toInt(), 0, 2000);
        cfgKickMaxApply   = clampI32(tok[3].toInt(), 0, 100);
      } else {
        cfgKickRampDownMs = 80;
        cfgKickMaxApply   = 15;
      }

      if (debugMode) {
        dbgPrintf("[CFG] KICK(MAPPED)=%ld,%ld rd=%ld max=%ld",
                  (long)cfgKickThrottle,
                  (long)cfgKickMs,
                  (long)cfgKickRampDownMs,
                  (long)cfgKickMaxApply);
      }

      if (cfgKickThrottle != oldThr || cfgKickMs != oldMs || cfgKickRampDownMs != oldRd || cfgKickMaxApply != oldMax) {
        markConfigDirty();
      }

      sendACK(preserved);
      return;
    }

    // STOPS (honor FLOOR snap-to-stop behavior)
    if (upper == "S") {
      if (allowMotionNow) executeStopRamp(RampKind::QUICKSTOP, /*honorMinstartFloorSnap*/true);
      else stopMotorNow("Forced-stop latched; S keeps stopped");
      sendACK(preserved);
      return;
    }
    if (upper == "B") {
      if (allowMotionNow) executeStopRamp(RampKind::BRAKE, /*honorMinstartFloorSnap*/true);
      else stopMotorNow("Forced-stop latched; B keeps stopped");
      sendACK(preserved);
      return;
    }

    // QUICK RAMP: FQ / RQ
    if (upper.startsWith("FQ") || upper.startsWith("RQ")) {
      if (!handshakeOk) { sendERR("InvalidCMD"); return; }

      String nStr = original.substring(2);
      nStr.trim();
      if (!isDigitStr(nStr)) { sendERR(preserved); return; }

      int32_t n = clampI32(nStr.toInt(), 0, 100);

      if (allowMotionNow) {
        if (forcedStopLatched && bleConnected) forcedStopLatched = false;
        executeQuickRamp(upper.startsWith("FQ") ? Direction::FWD : Direction::REV, n);
      } else {
        stopMotorNow("Forced-stop latched; motion ignored until reconnect");
      }

      sendACK(preserved);
      return;
    }

    // MOMENTUM: F / R
    if (upper.startsWith("F") || upper.startsWith("R")) {
      if (upper.startsWith("FQ") || upper.startsWith("RQ")) { sendERR(preserved); return; }

      if (!handshakeOk) { sendERR("InvalidCMD"); return; }

      String nStr = original.substring(1);
      nStr.trim();
      if (!isDigitStr(nStr)) { sendERR(preserved); return; }

      int32_t n = clampI32(nStr.toInt(), 0, 100);

      if (allowMotionNow) {
        if (forcedStopLatched && bleConnected) forcedStopLatched = false;
        executeMomentum(upper.startsWith("F") ? Direction::FWD : Direction::REV, n);
      } else {
        stopMotorNow("Forced-stop latched; motion ignored until reconnect");
      }

      sendACK(preserved);
      return;
    }
        // Unknown command fallback: reply with ERR:<original>
    sendERR(preserved);
    return;
  }
};

// ------------------------- Setup & Loop -------------------------
static void setupPwm() {
  ledcSetup(PWM_CH_R, PWM_FREQ_HZ, PWM_RES_BITS);
  ledcSetup(PWM_CH_L, PWM_FREQ_HZ, PWM_RES_BITS);
  ledcAttachPin(PIN_RPWM, PWM_CH_R);
  ledcAttachPin(PIN_LPWM, PWM_CH_L);
  pwmInitialized = true;
  applyPwmOutputs(Direction::STOP, 0);

  if (debugMode) {
    dbgPrintf("[PWM] init ok, dutyR=%lu dutyL=%lu",
              (unsigned long)ledcRead(PWM_CH_R),
              (unsigned long)ledcRead(PWM_CH_L));
  }
}

static void setupDriverPins() {
  // Configure EN pins ----
  pinMode(PIN_REN, OUTPUT);
  pinMode(PIN_LEN, OUTPUT);
  driverEnable(false);   // Start DISABLED (true coast/off)
}

static void setupBle() {
  // Init device name as advertised name (cfgTrainName if set, else FW_NAME)
  String advName = clampAdvName(getAdvertisedName());
  NimBLEDevice::init(advName.c_str());

  pServerGlobal = NimBLEDevice::createServer();
  pServerGlobal->setCallbacks(new ServerCallbacks());

  NimBLEService* svc = pServerGlobal->createService(SERVICE_UUID);

  pTxChar = svc->createCharacteristic(TX_UUID, NIMBLE_PROPERTY::NOTIFY);

  NimBLECharacteristic* rx = svc->createCharacteristic(
    RX_UUID,
    NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_NR
  );
  rx->setCallbacks(new RxCallbacks());

  svc->start();

  NimBLEAdvertising* adv = NimBLEDevice::getAdvertising();
  adv->addServiceUUID(SERVICE_UUID);
  adv->setName(advName.c_str());
  adv->start();

  debugPrintln("[BLE] Advertising");
}

void setup() {
  if (debugMode) {
    Serial.begin(115200);
    delay(50);
  }

  ledInit();      // LED starts in "disconnected blink" mode by ledService()
  setupDriverPins();
  setupPwm();

  // Cache eFuse MAC once at startup (do not query on-demand)
  {
    uint64_t mac = ESP.getEfuseMac(); // 48-bit MAC in the low bits
    // Format as 12 hex chars (lowercase), zero-padded
    char buf[13];
    snprintf(buf, sizeof(buf), "%012llx", (unsigned long long)(mac & 0xFFFFFFFFFFFFULL));
    gEfuseHex = String(buf);
  }

  loadConfigFromNvs();

  // Make sure advertised name matches cfgTrainName immediately at boot
  // (If BLE isn't initialized yet, this will be applied during setupBle().)

  stopMotorNow("Boot");

  if (debugMode) {
    lastLoggedAppliedThrottle = -9999;
    lastLoggedDirection = (Direction)255;

    dbgPrintf("[DEBUG] ON (startup)");
    dbgPrintf("[FW] %s v%s", FW_NAME, FW_VERSION);
    dbgPrintf("[BOOT] ready");
  }

  setupBle();

  // After BLE is up, ensure adv name is exactly correct (covers edge cases where cfgTrainName was loaded).
  restartAdvertisingWithName();
}

// ------------------------- Main loop tasks -------------------------
static void processRamp() {
  if (!rampActive) return;

  uint32_t now = millis();
  uint32_t elapsed = now - rampStartMs;

  int32_t newThr = smoothstepEasedThrottle(
      rampStartThrottle,
      rampTargetThrottle,
      elapsed,
      rampDurationMs);

  // Apply ramp in MAPPED domain; output mapping happens inside applyPwmOutputs().
  setApplied(rampDirection, newThr, "Ramp tick");
  applyPwmOutputs(currentDirection, appliedThrottle);

  if (elapsed >= rampDurationMs) {
    rampActive = false;

    // Snap to final mapped target
    setApplied(rampDirection, rampTargetThrottle, "Ramp complete");
    applyPwmOutputs(currentDirection, appliedThrottle);

    // If this ramp was a stop-to-floor (mapped=1) with snap-to-stop staged, do it now.
    if (stopSnapAfterRamp && cfgMinStart > 0 && rampTargetThrottle == 1) {
      stopSnapAfterRamp = false;
      stopMotorNow(stopSnapReason);
      return;
    }

    // Normal reverse arming only when we truly ramped to 0
    if (reversePending && (rampTargetThrottle == 0)) {
      pendingStage = PendingStage::WAIT_DIR_DELAY;
      pendingStageUntilMs = millis() + (pendingSkipDirDelay ? 0 : DIR_CHANGE_DELAY_MS);
      pendingSkipDirDelay = false;
    }
  }
}

static void processPendingReverse() {
  if (!reversePending) return;
  if (pendingStage != PendingStage::WAIT_DIR_DELAY) return;

  uint32_t now = millis();
  if ((int32_t)(now - pendingStageUntilMs) < 0) return;

  pendingStage = PendingStage::NONE;

  // Consume one-shot suppression immediately so it can't stick.
  const bool suppressKick = pendingSuppressKickOnce;
  pendingSuppressKickOnce = false;

  Direction dir = pendingFinalDirection;
  int32_t thr = clampI32(pendingFinalTargetThrottle, 0, 100);

  bool startingFromStop = (appliedThrottle == 0 && currentDirection == Direction::STOP);
  int32_t effThr = effectiveStartTargetThrottle(thr, startingFromStop);

  setTarget(dir, effThr, "Reverse complete target");

  if (!suppressKick && shouldKickOnStart(startingFromStop, effThr)) {
    beginKick(dir, effThr,
      pendingFinalIsInstant,
      pendingFinalIsMomentum,
      pendingFullAccelMs,
      pendingFullDecelMs);
    return;
  }

  reversePending = false;

  if (pendingFinalIsInstant) {
    setApplied(dir, effThr, "Reverse complete -> instant");
    applyPwmOutputs(currentDirection, appliedThrottle);
    return;
  }

  if (pendingFinalIsMomentum) {
    uint32_t dur = scaledDurationMs(pendingFullAccelMs, abs(effThr));
    startRamp(dir, 0, effThr, dur, RampKind::MOMENTUM);
    return;
  }

  setApplied(dir, effThr, "Reverse complete -> apply");
  applyPwmOutputs(currentDirection, appliedThrottle);
}

static void processKick() {
  if (!kickActive) return;

  uint32_t now = millis();
  if ((int32_t)(now - kickEndMs) < 0) {
    setApplied(kickDirection, kickHoldThrottle, "KICK hold");
    applyPwmOutputs(currentDirection, appliedThrottle);
    return;
  }

  kickActive = false;
  continueAfterKickIfNeeded();
}

static void processBleGrace() {
  if (!graceActive) return;

  uint32_t now = millis();

  // If disconnectMs was never refreshed or is invalid, restart grace cleanly.
  if (disconnectMs == 0 || now < disconnectMs) {
    disconnectMs = now;
  }

  if (debugMode) {
    if (!graceCountdownPrimed ) {
      // first time we enter grace window
      graceCountdownPrimed = true;
      graceLastCountdownLogMs = 0; // force immediate log
    }

    if (graceLastCountdownLogMs == 0 || (now - graceLastCountdownLogMs) >= GRACE_COUNTDOWN_LOG_PERIOD_MS) {
      graceLastCountdownLogMs = now;

      uint32_t elapsed = now - disconnectMs;
      uint32_t remaining = (elapsed >= BLE_GRACE_MS) ? 0 : (BLE_GRACE_MS - elapsed);

      dbgPrintf("[BLE] Grace countdown: %lums remaining", (unsigned long)remaining);
    }
  } else {
    // If debug is off, don't keep stale primed state
    graceCountdownPrimed = false;
    graceLastCountdownLogMs = 0;
  }

  // --- Timeout behavior ---
  if ((now - disconnectMs) >= BLE_GRACE_MS) {
    graceActive = false;

    // reset countdown state for next disconnect
    graceCountdownPrimed = false;
    graceLastCountdownLogMs = 0;

    forcedStopLatched = true;

    // "act like S was sent" behavior (honor FLOOR snap-to-stop behavior)
    executeStopRamp(RampKind::QUICKSTOP, /*honorMinstartFloorSnap*/true);

    if (debugMode) {
      dbgPrintf("[BLE] Grace expired: forced stop latched");
    }
  }
}

void loop() {
  // Keep LED behavior running
  ledService();

  processBleGrace();
  processKick();
  processRamp();
  processPendingReverse();

  if (!rampActive && !kickActive) {
    if (appliedThrottle == 0) currentDirection = Direction::STOP;
    applyPwmOutputs(currentDirection, appliedThrottle);
  }

  processConfigSaveIfNeeded();

  // ---- DEBUG SNAPSHOT HOOK (non-control, debug only) ----
  if (debugMode && pwmInitialized) {
    logThrottleChangeIfNeeded(g_lastDbgReason);
  }
}