Excellent — this is the right move. Below is a **production-grade, repeatable, 100% deterministic setup guide** for:

> macOS (Apple Silicon) → .NET MAUI → iOS Simulator → Physical iPhone → GitHub → VS Code

This is written so another developer can follow it and succeed without improvisation.

---

# macOS → .NET MAUI → iOS Deployment Setup Guide

**Target Machine:** Apple Silicon Mac (arm64)
**Target Framework:** .NET 10 + .NET MAUI
**IDE:** VS Code
**Target Device:** iPhone (physical + simulator)
**Source Control:** GitHub (SSH)

---

# Table of Contents

1. Prerequisites
2. Install Core Tooling
3. Configure GitHub (SSH)
4. Install .NET SDK + MAUI
5. Install Xcode + iOS Toolchain
6. Create iOS Simulator (Specific Device)
7. Validate MAUI iOS Build
8. Fix Known Simulator Install Issue (Exit 149)
9. VS Code Configuration
10. Connect to Existing GitHub Repo
11. Physical iPhone Deployment Prep
12. Expected Results Checklist
13. Troubleshooting Matrix

---

# 1️⃣ Prerequisites

### Hardware

* Apple Silicon Mac (M1/M2/M3/M4)
* macOS 13+ (you are on 26.1)
* iPhone (for BLE testing)

### Apple Requirements

* Apple ID
* Active Apple Developer Program membership ($99/year)

Verify:

```bash
sw_vers
uname -m
```

Expected:

* arm64
* macOS version displayed

---

# 2️⃣ Install Core Tooling

## 2.1 Install Homebrew (Package Manager)

Link:
[https://brew.sh](https://brew.sh)

Install:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

Verify:

```bash
brew --version
```

Expected:

```
Homebrew 4.x.x
```

---

## 2.2 Install Git (if not present)

```bash
brew install git
```

Verify:

```bash
git --version
```

Expected:

```
git version X.X.X
```

---

## 2.3 Install GitHub CLI

```bash
brew install gh
```

Verify:

```bash
gh --version
```

---

# 3️⃣ Configure GitHub (SSH – Recommended)

## 3.1 Login GitHub CLI

```bash
gh auth login
```

Choose:

* GitHub.com
* HTTPS (for CLI)
* Login with web browser

Verify:

```bash
gh auth status
```

---

## 3.2 Create SSH Key

```bash
ssh-keygen -t ed25519 -C "your_github_username"
```

Start agent:

```bash
eval "$(ssh-agent -s)"
ssh-add --apple-use-keychain ~/.ssh/id_ed25519
```

Test:

```bash
ssh -T git@github.com
```

Expected:

```
Hi <username>! You've successfully authenticated
```

---

# 4️⃣ Install .NET SDK + MAUI

## 4.1 Install .NET SDK (ARM64)

Download:
[https://dotnet.microsoft.com/en-us/download](https://dotnet.microsoft.com/en-us/download)

Install ARM64 macOS SDK.

Verify:

```bash
dotnet --info
```

Expected:

* SDK listed
* RID: osx-arm64

---

## 4.2 Install MAUI Workload

```bash
sudo dotnet workload install maui
```

Verify:

```bash
dotnet workload list
```

Expected:

```
maui 10.x.x
```

---

## 4.3 Trust HTTPS Dev Certificate

```bash
dotnet dev-certs https --trust
```

Expected:

```
Successfully trusted the existing HTTPS certificate.
```

---

# 5️⃣ Install Xcode + iOS Toolchain

## 5.1 Install Xcode

Install from:
Mac App Store → Search "Xcode"

---

## 5.2 Set Active Developer Directory

```bash
sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
xcode-select -p
```

Expected:

```
/Applications/Xcode.app/Contents/Developer
```

---

## 5.3 Accept License

```bash
sudo xcodebuild -license accept
sudo xcodebuild -runFirstLaunch
```

---

## 5.4 Verify SDKs

```bash
xcodebuild -showsdks | egrep "iphoneos|iphonesimulator"
```

Expected:

```
iOS XX.X -sdk iphoneosXX.X
Simulator - iOS XX.X -sdk iphonesimulatorXX.X
```

---

## 5.5 Install iOS Simulator Runtime

Open:
Xcode → Settings → Platforms

Download:
Latest iOS Simulator runtime.

Verify:

```bash
xcrun simctl list runtimes | grep iOS
```

Expected:

```
iOS XX.X (...)
```

---

# 6️⃣ Create Specific Simulator (Example: iPhone 13 Pro Max)

Find runtime ID:

```bash
xcrun simctl list runtimes | grep iOS
```

Create simulator:

```bash
xcrun simctl create "iPhone 13 Pro Max (iOS XX.X)" \
com.apple.CoreSimulator.SimDeviceType.iPhone-13-Pro-Max \
com.apple.CoreSimulator.SimRuntime.iOS-XX-X
```

Boot it:

```bash
xcrun simctl boot "<UDID>"
open -a Simulator
```

Verify:

```bash
xcrun simctl list devices
```

Expected:

```
(Booted)
```

---

# 7️⃣ Validate MAUI iOS Build

Create test app:

```bash
dotnet new maui -n MauiSmokeTest
cd MauiSmokeTest
```

Build:

```bash
dotnet build -f net10.0-ios -c Debug
```

Expected:

```
Build succeeded
```

Install manually:

```bash
xcrun simctl install <UDID> bin/Debug/net10.0-ios/iossimulator-arm64/MauiSmokeTest.app
```

Launch:

```bash
xcrun simctl launch <UDID> com.companyname.mauismoketest
```

Expected:
App opens in Simulator.

---

# 8️⃣ Known Issue: simctl Exit Code 149

If:

```
HE0046 simctl returned exit code 149
```

Solution:

```bash
xcrun simctl uninstall <UDID> com.companyname.mauismoketest
xcrun simctl erase <UDID>
sudo killall -9 com.apple.CoreSimulator.CoreSimulatorService
```

Then reinstall manually.

This resolves corrupted simulator state.

---

# 9️⃣ Install VS Code

Download:
[https://code.visualstudio.com](https://code.visualstudio.com)

Verify:

```bash
code --version
```

---

## Required Extensions

Install:

```bash
code --install-extension ms-dotnettools.csdevkit
code --install-extension ms-dotnettools.csharp
code --install-extension ms-dotnettools.dotnet-maui
code --install-extension GitHub.vscode-pull-request-github
```

Verify:

```bash
code --list-extensions
```

---

# 🔟 Connect to GitHub Repo

Clone:

```bash
gh repo clone OWNER/REPO
cd REPO
```

Switch to SSH (recommended):

```bash
git remote set-url origin git@github.com:OWNER/REPO.git
git remote -v
```

Expected:

```
git@github.com:...
```

Open in VS Code:

```bash
code .
```

Verify:

* Source Control panel active
* Correct branch
* Working tree clean

---

# 1️⃣1️⃣ Physical iPhone Deployment Prep

## Enable Developer Mode

On iPhone:
Settings → Privacy & Security → Developer Mode → ON → Restart

---

## Connect Device

Plug in iPhone.

Trust computer.

Verify:

```bash
xcrun xctrace list devices
```

Expected:

```
iPhone 13 Pro Max (...)
```

---

## Xcode Device Registration

Open:
Xcode → Window → Devices and Simulators

Confirm:

* Device appears
* No warnings

---

# 1️⃣2️⃣ Final Success Checklist

✅ Homebrew installed
✅ Git + GitHub CLI configured
✅ SSH authentication working
✅ .NET SDK installed
✅ MAUI workload installed
✅ Xcode installed + licensed
✅ iOS Simulator runtime installed
✅ Specific simulator created
✅ MAUI app builds
✅ MAUI app launches in simulator
✅ VS Code configured
✅ GitHub repo connected
✅ iPhone Developer Mode enabled

---

# 1️⃣3️⃣ Troubleshooting Matrix

| Issue                 | Cause              | Fix                     |
| --------------------- | ------------------ | ----------------------- |
| dotnet not found      | PATH issue         | reinstall SDK           |
| simctl 149            | corrupt simulator  | erase + restart service |
| Device not showing    | Developer Mode off | enable + reboot         |
| SSH permission denied | key not added      | ssh-add                 |
| iOS SDK missing       | Xcode not active   | xcode-select            |


