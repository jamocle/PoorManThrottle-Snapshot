## Simulator & Device Deployment Cheat Sheet (MAUI iOS / .NET 10 / macOS)

> Assumes you’re in repo root `PoorManThrottle/Maui` and your app project is `PoorManThrottle.App/PoorManThrottle.App.csproj`.

---

# 1) Find simulators (no guessing)

### List all available simulators

```bash
xcrun simctl list devices available
```

### Find a specific model (example: iPhone 13 Pro Max)

```bash
xcrun simctl list devices available | grep -n "iPhone 13 Pro Max" || true
```

### Extract the UDID only (optional)

```bash
xcrun simctl list devices available | grep "iPhone 13 Pro Max" | head -n 1
```

---

# 2) Boot / shutdown / reset a simulator

### Open Simulator app

```bash
open -a Simulator
```

### Boot a simulator by UDID (deterministic)

```bash
xcrun simctl boot <UDID>
```

### Shutdown a simulator

```bash
xcrun simctl shutdown <UDID>
```

### Erase (factory reset) a simulator

```bash
xcrun simctl erase <UDID>
```

### See what’s currently booted

```bash
xcrun simctl list | grep -n "Booted" || true
```

---

# 3) Build MAUI for iOS simulator

### Build iOS target (simulator)

```bash
dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Debug -f net10.0-ios
```

### Force simulator architecture (Apple Silicon)

```bash
dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Debug -f net10.0-ios -r iossimulator-arm64
```

### Locate the built `.app` bundle (handy for debugging)

```bash
find PoorManThrottle.App/bin/Debug -maxdepth 6 -type d -name "*.app" -print
```

---

# 4) Run on a specific simulator (best-practice, pinned UDID)

## Recommended: MSBuild Run target (most reliable)

```bash
dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj \
  -c Debug -f net10.0-ios \
  -t:Run \
  -p:_DeviceName=:v2:udid=<UDID>
```

✅ Use this when you want “known quantities” and zero ambiguity.

---

# 5) When `dotnet run` fails (common pitfall)

If you see errors like:

* `error MT0069: The app directory ... .app does not exist`
* or it’s using `launchSettings.json` unexpectedly

Use the **MSBuild Run target** above instead (Section 4). It bypasses most launchSettings edge cases.

---

# 6) Real iPhone (physical device) deployment

## First: verify your device is connected + trusted

Open Xcode once and confirm the device appears under:

* **Window → Devices and Simulators**

## Then build/run (most common MAUI path)

You typically run to device via Visual Studio for Mac alternatives / Xcode pipeline, but via CLI you’ll still use MSBuild `Run` with device selection.

### List available destinations (Xcode tooling)

This one is more “native iOS” but useful:

```bash
xcrun xcodebuild -showdestinations -scheme PoorManThrottle.App 2>/dev/null || true
```

> MAUI projects don’t always map 1:1 to a scheme name you expect. If this is noisy, we’ll use `dotnet build -t:Run` and choose device via `_DeviceName`.

### Practical approach we’ll use later (deterministic):

* We’ll identify the device’s UDID via Xcode Devices window (or `xcrun xctrace list devices`)
* Then run:

```bash
xcrun xctrace list devices
```

Look for your iPhone entry and its UDID, then:

```bash
dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj \
  -c Debug -f net10.0-ios \
  -t:Run \
  -p:_DeviceName=:v2:udid=<IPHONE_UDID>
```

---

# 7) Quick “iPhone 13 Pro Max simulator” workflow (your exact device)

Given your known UDID:
`A3F5CCAF-DF5C-454C-86FE-98D66A7D0FFA`

### Boot + Run

```bash
open -a Simulator
xcrun simctl boot A3F5CCAF-DF5C-454C-86FE-98D66A7D0FFA

dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj \
  -c Debug -f net10.0-ios \
  -t:Run \
  -p:_DeviceName=:v2:udid=A3F5CCAF-DF5C-454C-86FE-98D66A7D0FFA
```

---

# 8) Common deployment troubleshooting

### Simulator is stuck / app won’t install

* Reset simulator:

```bash
xcrun simctl erase <UDID>
```

### Wrong simulator being used

* Ensure you always use the pinned UDID with `_DeviceName=:v2:udid=...` (Section 4)

### Build failing due to Android SDK (while iOS-first)

* Build iOS only:

```bash
dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Debug -f net10.0-ios
```

