<table>
  <tr>
    <th>Description</th>
    <th>Command</th>
    <th>Notes</th>
  </tr>
  <tr>
    <td>Display directory tree while excluding specific folders</td>
    <td><pre><code>tree -I "bin|obj|.git"</code></pre></td>
    <td>
      Displays a recursive tree view of the current directory and its subdirectories using the tree utility.
      The -I option tells tree to ignore files or directories that match a pattern.
      The pattern "bin|obj|.git" uses the pipe character (|) as a separator, meaning any directory or file named
      bin, obj, or .git will be excluded from the output.
      This is commonly used in development environments to hide build output folders (bin, obj) and version control
      metadata directories (.git) so the structural view focuses only on relevant source files.
    </td>
  </tr>
  <tr>
    <td>Build a .NET MAUI iOS project for a physical ARM64 device device using the Debug configuration (without deploying or running)</td>
    <td><pre><code>dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Debug -f net10.0-ios -r ios-arm64</code></pre></td>
    <td>
      Uses the dotnet CLI to compile the specified .NET MAUI project file targeting iOS.
      The project path points to PoorManThrottle.App.csproj.
      -c Debug selects the Debug build configuration.
      -f net10.0-ios sets the target framework to .NET 10 for iOS.
      -r ios-arm64 specifies the runtime identifier, targeting physical ARM64 iOS devices rather than the simulator.
      Unlike commands that include MSBuild targets such as -t:Run, this command only performs a build and does not
      deploy or launch the application.
      The output artifacts (including the generated .app bundle) will be placed under the project's bin/Debug
      directory for the specified framework and runtime.
      Proper Apple code signing configuration is still required for a successful device build.
    </td>
  </tr>
  <tr>
    <td>Build a .NET MAUI iOS project for a physical ARM64 device using the Release configuration (without deploying or running)</td>
    <td><pre><code>dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -c Release -f net10.0-ios -r ios-arm64</code></pre></td>
    <td>
      Uses the dotnet CLI to compile the specified .NET MAUI iOS project in Release mode.
      The project path points to PoorManThrottle.App.csproj.
      -c Release selects the Release build configuration, which typically enables compiler optimizations
      and disables debugging symbols compared to Debug builds.
      -f net10.0-ios sets the target framework to .NET 10 for iOS.
      -r ios-arm64 specifies the runtime identifier targeting physical ARM64 iOS devices.
      This command performs only a build and does not deploy or launch the application.
      The resulting optimized build artifacts, including the .app bundle, will be generated under
      the bin/Release directory for the specified framework and runtime.
      Proper Apple code signing and provisioning configuration is still required for successful device builds.
      <div style="background-color: yellow; color: black; font-weight: bold; padding: 6px; display: inline-block;">
        Build succeeded with 2 warning(s) in 115.3s
      </div>
    </td>
  </tr>
  <tr>
    <td>Install a compiled iOS app bundle onto a specific connected device using Xcode command-line tools</td>
    <td><pre><code>xcrun devicectl device install app --device 00008110-0012652A36C1801E "PoorManThrottle.App/bin/Debug/net10.0-ios/ios-arm64/PoorManThrottle.App.app"</code></pre></td>
    <td>
      Uses xcrun to invoke devicectl, Apple’s command-line utility for interacting with physical iOS devices.
      The subcommand "device install app" installs an .app bundle directly onto a connected device.
      The --device flag specifies the target device by its unique device identifier (UDID), in this case
      00008110-0012652A36C1801E.
      The final argument is the path to the compiled .app bundle that will be installed.
      The path indicates a .NET MAUI iOS build output located under bin/Debug targeting net10.0-ios for the ios-arm64
      architecture. The .app directory must already be properly built and code-signed for installation to succeed.
    </td>
  </tr>
  <tr>
    <td>Recursively search for iOS .app bundles in a build output directory</td>
    <td><pre><code>find PoorManThrottle.App/bin/Debug/net10.0-ios/ios-arm64 -name "*.app"</code></pre></td>
    <td>
      Uses the find command to recursively search the specified directory path for files or directories
      matching a given name pattern.
      The search root is PoorManThrottle.App/bin/Debug/net10.0-ios/ios-arm64, which appears to be
      a .NET MAUI iOS build output directory targeting ios-arm64.
      The -name option filters results to entries whose names match the glob pattern "*.app".
      The quoted pattern ensures the shell does not expand the wildcard before passing it to find.
      This is commonly used to locate the generated .app bundle after a successful build so it can be
      installed, inspected, or packaged.
    </td>
  </tr>
  <tr>
    <td>Build and run a .NET MAUI iOS project for a specific physical device</td>
    <td><pre><code>dotnet build PoorManThrottle.App/PoorManThrottle.App.csproj -f net10.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64 -p:_DeviceName=:v2:udid=00008110-0012652A36C1801E -t:Build -t:Run</code></pre></td>
    <td>
      Uses the dotnet CLI to build a specific project file (PoorManThrottle.App.csproj).
      The path points to a .NET MAUI application project targeting iOS.
      -f net10.0-ios sets the target framework to .NET 10 for iOS.
      -c Debug selects the Debug configuration.
      -p:RuntimeIdentifier=ios-arm64 targets physical ARM64 iOS devices.
      -p:_DeviceName=:v2:udid=00008110-0012652A36C1801E specifies the exact device by UDID.
      -t:Build runs the MSBuild Build target.
      -t:Run runs the Run target, which deploys and launches the app after building.
      This command builds, deploys, and launches the app on the specified connected device.
      Proper Apple code signing and provisioning profiles must already be configured.
    </td>
  </tr>
  <tr>
    <td>Aggregate selected project source files into a single combined text file</td>
    <td><pre><code>find "$(pwd)" -type f \( -name "*.sln" -o -name "*.csproj" -o -name "*.cs" -o -name "*.xaml" -o -name "*.json" -o -name "*.props" -o -name "*.targets" -o -name "*.config" \) ! -path "*/bin/*" ! -path "*/obj/*" ! -path "*/.git/*" ! -name "wholecode.txt" -print0 | while IFS= read -r -d '' file; do rel="${file#$(pwd)/}"; echo "===== $rel ====="; cat "$file"; echo; done > wholecode.txt</code></pre></td>
    <td>
      Recursively searches from the current working directory (using "$(pwd)") for specific project-related
      file types and concatenates their contents into a single output file named wholecode.txt.
      -type f restricts results to regular files.
      The grouped expression \( ... \) matches multiple extensions using -name combined with -o (logical OR),
      including solution files, C# source files, XAML, JSON, MSBuild props/targets, and config files.
      The ! -path filters exclude files inside bin, obj, and .git directories.
      The ! -name "wholecode.txt" prevents the output file from being re-read into itself.
      -print0 outputs null-delimited file paths to safely handle spaces and special characters.
      The output is piped into a while loop that reads each filename safely (IFS= and -d '' preserve exact names).
      rel="${file#$(pwd)/}" strips the absolute path prefix to create a relative display header.
      Each file’s path is printed as a header line (===== relative/path =====), followed by its contents via cat.
      The entire combined output is redirected into wholecode.txt.
      This is useful for generating a single consolidated source snapshot for review, auditing, or AI analysis.
    </td>
  </tr>
  <tr>
    <td>Run all tests in a specific .NET test project</td>
    <td><pre><code>dotnet test /Volumes/OWC\ Express\ 1M2/Dev/git/PoorManThrottle/Maui/PoorManThrottle.Tests/PoorManThrottle.Tests.csproj</code></pre></td>
    <td>
      Uses the dotnet CLI to build (if necessary) and execute all tests defined in the specified test project file.
      The argument is the full path to PoorManThrottle.Tests.csproj located on an external volume named
      "OWC Express 1M2" under /Volumes.
      Spaces in the volume name are escaped using backslashes (\) so the shell interprets the path correctly
      as a single argument.
      By default, dotnet test performs a restore (if needed), builds the project, runs the tests using the
      configured test framework (such as xUnit, NUnit, or MSTest), and outputs a summary of passed, failed,
      and skipped tests.
      This command is typically used during development or CI to validate application behavior through
      automated unit or integration tests.
    </td>
  </tr>
</table>