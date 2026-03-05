Below is a consolidated reference table of the **terminal commands we have used so far in this chat**, including:

* ✅ The command
* 📖 What it does
* 🎯 Why we used it

You can paste this into your `GitCommands.md` or a new `Maui/TerminalReference.md`.

---

# Poor Man’s Throttle – Terminal Command Reference

## 🧭 Environment & SDK Inspection

| Command                  | What It Does                                                    | Why We Used It                                                      |
| ------------------------ | --------------------------------------------------------------- | ------------------------------------------------------------------- |
| `dotnet --info`          | Displays installed .NET SDKs, runtimes, and environment details | To confirm you were running .NET 10 SDK and verify platform support |
| `dotnet workload list`   | Lists installed workloads (e.g., MAUI)                          | To confirm MAUI workload was installed                              |
| `dotnet workload update` | Updates installed workloads to latest compatible versions       | Attempted to resolve MAUI dependency mismatch                       |
| `dotnet workload repair` | Repairs broken workload installations                           | Used when toolkit version mismatch persisted                        |

---

## 📁 File System Inspection

| Command                                                                                       | What It Does                                                             | Why We Used It                                                                                                         |
| --------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- |
| `ls`                                                                                          | Lists files in current directory                                         | To inspect repo structure                                                                                              |
| `ls -la`                                                                                      | Lists files including hidden with details                                | To confirm project folders and solution file                                                                           |
| `cd <folder>`                                                                                 | Changes directory                                                        | To navigate into Maui project                                                                                          |
| `find . -maxdepth 3 -name "*.csproj" -print`                                                  | Finds project files                                                      | To confirm all projects existed                                                                                        |
| `find . -maxdepth 6 -type d -name "*.app" -print`                                             | Finds compiled iOS app bundles                                           | To verify build output location                                                                                        |
| `find . -maxdepth 3 -type f \( -name "*.cs" -o -name "*.xaml" -o -name "*.csproj" \) \| sort` | Lists all C#, XAML, and project files (sorted) within 3 directory levels | To generate a clean snapshot of current project structure for review or to share accurate state during troubleshooting |

---
## 🧱 Solution Management (.NET 10 uses .slnx)

| Command                                         | What It Does                                     | Why We Used It                              |
| ----------------------------------------------- | ------------------------------------------------ | ------------------------------------------- |
| `dotnet new sln -n PoorManThrottle`             | Creates a new solution file (`.slnx` in .NET 10) | To initialize solution container            |
| `dotnet sln PoorManThrottle.slnx list`          | Lists projects in solution                       | To verify projects were added               |
| `dotnet sln PoorManThrottle.slnx add <project>` | Adds project to solution                         | To include App, Core, Infrastructure, Tests |

---

## 🔨 Building Projects

| Command                                                                                       | What It Does                              | Why We Used It                                                |
| --------------------------------------------------------------------------------------------- | ----------------------------------------- | ------------------------------------------------------------- |
| `dotnet build PoorManThrottle.slnx -c Debug`                                                  | Builds entire solution                    | Initial build attempt (Android failed)                        |
| `dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Debug -f net10.0-ios`         | Builds iOS target only                    | To bypass Android SDK error                                   |
| `dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Debug -f net10.0-maccatalyst` | Builds Mac Catalyst target                | To verify Mac target compiled                                 |
| `dotnet build -t:Run -f net10.0-ios -p:_DeviceName=:v2:udid=<UDID>`                           | Builds and runs on specific iOS simulator | Required because `dotnet run` failed due to app path mismatch |

---

## ▶️ Running on iOS Simulator

| Command                                                             | What It Does                                | Why We Used It                                     |
| ------------------------------------------------------------------- | ------------------------------------------- | -------------------------------------------------- |
| `xcrun simctl list devices available`                               | Lists available simulators                  | To find iPhone 13 Pro Max simulator UDID           |
| `open -a Simulator`                                                 | Opens iOS Simulator app                     | To ensure simulator was running                    |
| `xcrun simctl boot <UDID>`                                          | Boots specific simulator                    | To ensure correct device is active                 |
| `dotnet run --project ... -- -device:<UDID>`                        | Attempts to run on simulator                | Initial run attempt (failed due to app path issue) |
| `dotnet build -t:Run -f net10.0-ios -p:_DeviceName=:v2:udid=<UDID>` | Correct way to deploy to specific simulator | Successfully launched app                          |

---

## 📦 NuGet Package Management

| Command                                                     | What It Does                | Why We Used It                            |
| ----------------------------------------------------------- | --------------------------- | ----------------------------------------- |
| `dotnet add package CommunityToolkit.Maui`                  | Adds toolkit package        | Initial install (caused version mismatch) |
| `dotnet remove package CommunityToolkit.Maui`               | Removes package             | To fix dependency conflict                |
| `dotnet add package CommunityToolkit.Maui --version 13.0.0` | Installs compatible version | Resolved `NU1605` version downgrade error |

---

## 🔍 Git Diagnostics (Recommended for Future Syncing)

| Command             | What It Does         | Why It’s Useful                              |
| ------------------- | -------------------- | -------------------------------------------- |
| `git status`        | Shows modified files | To understand current changes                |
| `git diff`          | Shows code changes   | Best way to “refresh” me with accurate state |
| `git log --oneline` | Shows commit history | To track step checkpoints                    |

---

# 🧠 Important Build Observations

### Android Failure

When building entire solution:

```
error XA5300: The Android SDK directory could not be found
```

Reason:

* Android SDK not installed locally.
* We chose to focus on iOS only for now.

Solution:

* Build specific target:

  ```
  -f net10.0-ios
  ```

---

# 🎯 Recommended Standard iOS Run Command

For your project:

```bash
dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj \
  -c Debug \
  -f net10.0-ios \
  -t:Run \
  -p:_DeviceName=:v2:udid=A3F5CCAF-DF5C-454C-86FE-98D66A7D0FFA
```

This avoids Android entirely and directly deploys to your **iPhone 13 Pro Max simulator**.

