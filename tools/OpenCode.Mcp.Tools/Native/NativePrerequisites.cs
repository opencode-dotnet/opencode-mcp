using System.IO.Abstractions;

namespace OpenCode.Mcp.Tools.Native;

internal sealed class NativePrerequisites(IFileSystem files, NativeProcesses processes)
{
    public async Task<NativeToolchain> CheckAsync(string rid, NativeHost host, CancellationToken token)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" };
        var target = new NativeRid(rid);
        if (!target.IsWindows)
        {
            var compiler = await processes.RequireAsync("clang", ["--version"], environment, token).ConfigureAwait(false);
            var inspector = await processes.RequireAsync("nm", ["--version"], environment, token).ConfigureAwait(false);
            return new NativeToolchain("nm", target.IsMacOS ? ["-gU"] : ["-D", "--defined-only"], environment, compiler + "\n" + inspector);
        }
        var programFiles = host.Environment.GetValueOrDefault("ProgramFiles(x86)")
            ?? throw new InvalidOperationException("Missing prerequisite: ProgramFiles(x86) for Visual Studio Installer.");
        var installer = files.Path.Combine(programFiles, "Microsoft Visual Studio", "Installer");
        var vswhere = files.Path.Combine(installer, "vswhere.exe");
        if (!files.File.Exists(vswhere))
        {
            throw new InvalidOperationException("Missing prerequisite: " + vswhere);
        }
        var architecture = rid[4..];
        var component = architecture == "arm64" ? "Microsoft.VisualStudio.Component.VC.Tools.ARM64" : "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
        var installation = await processes.RequireAsync(vswhere, ["-latest", "-products", "*", "-requires", component, "-property", "installationPath"], environment, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(installation))
        {
            throw new InvalidOperationException("Missing prerequisite: " + component);
        }
        var tools = files.Path.Combine(installation, "VC", "Tools", "MSVC");
        var inspectorPath = files.Directory.EnumerateDirectories(tools).OrderByDescending(path => Version.Parse(files.Path.GetFileName(path)))
            .Select(path => files.Path.Combine(path, "bin", "Host" + architecture, architecture, "dumpbin.exe")).FirstOrDefault(files.File.Exists)
            ?? throw new InvalidOperationException("Missing prerequisite: dumpbin.exe for " + rid);
        environment["PATH"] = installer + files.Path.PathSeparator + host.Environment.GetValueOrDefault("PATH");
        var details = await processes.RequireAsync(inspectorPath, ["/headers", inspectorPath], environment, token).ConfigureAwait(false);
        var linker = files.Path.Combine(files.Path.GetDirectoryName(inspectorPath) ?? throw new InvalidOperationException("Missing MSVC directory."), "link.exe");
        var linkerDetails = await processes.RequireAsync(linker, ["/dump", "/headers", linker], environment, token).ConfigureAwait(false);
        var sdkRoot = files.Path.Combine(programFiles, "Windows Kits", "10", "Lib");
        var sdk = (files.Directory.Exists(sdkRoot) ? files.Directory.EnumerateDirectories(sdkRoot).OrderByDescending(path => Version.Parse(files.Path.GetFileName(path)))
            .FirstOrDefault(path => files.File.Exists(files.Path.Combine(path, "um", architecture, "kernel32.lib"))) : null)
            ?? throw new InvalidOperationException("Missing prerequisite: Windows SDK libraries for " + rid + ".");
        return new NativeToolchain(inspectorPath, ["/exports"], environment, installation + "\n" + inspectorPath + "\n" + details + "\n" + linkerDetails + "\nAvailable Windows SDK: " + sdk);
    }
}
